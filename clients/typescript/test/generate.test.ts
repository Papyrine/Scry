import { test } from "node:test";
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import { generate } from "../src/codegen/generate.ts";
import { readDescription } from "../src/codegen/cli.ts";
import type { ScryIntrospection, ScryMemberInfo, ScryTypeInfo } from "../src/wire.ts";
import { descriptionPath, fixtures, runtime } from "./fixtures.ts";
import { schema } from "./generated/test-model.ts";

for (const fixture of fixtures) {
  test(`what is generated for ${fixture.name} is what is committed`, async () => {
    const description = await readDescription(descriptionPath(fixture));
    const committed = await readFile(fixture.generated, "utf8");

    assert.equal(
      generate(description, { runtime, from: fixture.description }),
      committed,
      "Run `npm run fixtures`, and review the difference as the change it makes.");
  });
}

// The rule ModelSynthesizer and the C# generator both follow, so a query projecting nothing sends the
// same projection from every client.
test("a query projecting nothing projects a base's scalars first, then its own, in declared order", () => {
  assert.deepEqual(schema.models.VehicleQueryModel.defaultProjection, ["Id", "Name", "Wheels"]);
  assert.deepEqual(
    schema.models.EmployeeQueryModel.defaultProjection,
    ["Id", "Name", "Status", "Perks", "Active", "ManagerId", "DepartmentId", "Avatar"]);
});

test("a query projecting nothing projects no navigation, collection or attachment", () => {
  // Contract carries an attachment, Document, between Name and its capability.
  assert.deepEqual(schema.models.ContractQueryModel.defaultProjection, ["Id", "Name", "CanSealContract"]);
});

test("a member is described under its TypeScript name, with the name it travels under", () => {
  const employee = schema.models.EmployeeQueryModel.members;

  assert.deepEqual(employee.managerId, { name: "ManagerId", shape: { kind: "value", value: { scalar: "int" }, nullable: true } });
  assert.deepEqual(employee.manager, { name: "Manager", shape: { kind: "navigation", model: "EmployeeQueryModel" } });
  assert.deepEqual(employee.previousAddresses.shape, { kind: "rows", model: "AddressQueryModel" });
  assert.deepEqual(schema.models.OrderQueryModel.members.tags.shape, { kind: "values", value: { scalar: "string" }, nullable: false });
});

test("what the server marks about a member is carried", () => {
  assert.equal(schema.models.EmployeeQueryModel.members.avatar.sensitive, true);
  assert.deepEqual(schema.models.ContractQueryModel.members.document.shape, { kind: "attachment", contentType: "application/pdf" });
  assert.equal(schema.models.ContractQueryModel.members.canSealContract.capability, "SealContract");
  assert.deepEqual(schema.models.VehicleQueryModel.base, "AssetQueryModel");
});

test("an enum of the base class library is described with its values, which introspection does not carry", () => {
  assert.deepEqual(schema.enums.DayOfWeek.values, ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]);
  assert.equal(schema.enums.Perks.flags, true);
  assert.equal(schema.enums.Status.flags, false);
});

test("a command's payload is described as it travels, camel-cased", () => {
  const rename = schema.commands.renameShift;

  assert.equal(rename.name, "RenameShift");
  assert.equal(rename.target, "Shift");
  assert.deepEqual(rename.keys, ["Id"]);
  assert.deepEqual(Object.keys(rename.payload), ["id", "name"]);
  assert.equal(rename.obsolete, "Shifts are renamed through the rota.");
});

// A description of one source with one member, for the refusals below to vary.
function described(members: ScryMemberInfo[], change: (description: ScryIntrospection) => void = () => {}): ScryIntrospection {
  const description: ScryIntrospection = {
    version: 2,
    maxPageSize: 100,
    sources: [{ name: "Thing", kind: "Entity", model: "ThingQueryModel" }],
    types: [{ model: "ThingQueryModel", members }],
    enums: [],
    commands: [],
    queryEndpoint: "/api/query",
    queryUrlLimit: 4096,
    sqlPreview: false,
    schemaStamp: "stamp"
  };
  change(description);
  return description;
}

function member(name: string, typeDisplay: string, kind: Partial<ScryMemberInfo> = {}): ScryMemberInfo {
  return { name, typeDisplay, needsNullDefault: false, isNavigation: false, isCollection: false, ...kind };
}

const options = { runtime: "scry-client", from: "a test" };

test("a description of the supported version generates", () => {
  assert.match(generate(described([member("Id", "int")]), options), /export interface ThingQuery \{/);
});

test("a type display the generator does not know is refused, naming the member", () => {
  assert.throws(
    () => generate(described([member("Id", "global::System.Half")]), options),
    /'ThingQueryModel\.Id'.*'global::System\.Half'/);
});

test("a member whose display disagrees with what it is described as is refused", () => {
  assert.throws(
    () => generate(described([member("Self", "ThingQueryModel?")]), options),
    /described as one kind of member/);
});

test("two members that case alike are refused", () => {
  assert.throws(
    () => generate(described([member("Name", "string"), member("NAME", "string")]), options),
    /as 'name'/);
});

test("a member that cases like one it inherits is refused", () => {
  const base: ScryTypeInfo = { model: "BaseQueryModel", members: [member("Name", "string")] };
  const description = described([member("NAME", "string")], _ => {
    _.types = [base, { ..._.types[0]!, base: "BaseQueryModel" }];
  });

  assert.throws(() => generate(description, options), /reaches both/);
});

test("two sources read under one name are refused", () => {
  const description = described([member("Id", "int")], _ => {
    _.sources = [..._.sources, { name: "THING", kind: "Entity", model: "ThingQueryModel" }];
  });

  assert.throws(() => generate(description, options), /Two sources are read under 'thing'/);
});

test("an enum declared under a name a model's row takes is refused", () => {
  const description = described([member("Kind", "ThingRow")], _ => {
    _.enums = [{ name: "ThingRow", values: ["One"], underlying: "int" }];
  });

  assert.throws(() => generate(description, options), /declared as 'ThingRow'/);
});

test("a description of another version is refused", () => {
  const description = described([member("Id", "int")], _ => {
    _.version = 3;
  });

  assert.throws(() => generate(description, options), /introspection version 3/);
});

test("a model not named as every server model is is refused", () => {
  const description = described([member("Id", "int")], _ => {
    _.types = [{ ..._.types[0]!, model: "Thing" }];
    _.sources = [{ name: "Thing", kind: "Entity", model: "Thing" }];
  });

  assert.throws(() => generate(description, options), /is not named '\{Type\}QueryModel'/);
});

test("the command line writes what generate does", async () => {
  const [fixture] = fixtures;
  const directory = await mkdtemp(join(tmpdir(), "scry-codegen-"));
  try {
    const output = join(directory, "generated.ts");
    const input = descriptionPath(fixture!);
    await promisify(execFile)(process.execPath, [join(import.meta.dirname, "..", "src", "codegen", "cli.ts"), "--input", input, "--output", output, "--runtime", runtime]);

    const expected = generate(await readDescription(input), { runtime, from: input });
    assert.equal(await readFile(output, "utf8"), expected);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
