// Keyset paging: each page answers with an opaque cursor, which Next hands back so the server seeks
// past the last row rather than counting an offset. Forward only, as a cursor is.

import { fill, messageOf, scry, status } from "./client.ts";

const pageSize = 2;

// begin-snippet: typescriptWebKeyset
let cursor: string | undefined;

async function load(from: string | undefined): Promise<void> {
  const page = await scry.employee
    .orderBy(_ => _.name)
    .select(_ => ({ name: _.name, status: _.status, department: _.department.name }))
    .page(pageSize, from);

  cursor = page.cursor;
  fill("employees", page.items.map(_ => [_.name, _.status, _.department ?? ""]));
  next.disabled = !page.hasMore;
}
// end-snippet

const next = document.querySelector<HTMLButtonElement>("#next")!;

async function show(from: string | undefined): Promise<void> {
  try {
    await load(from);
  } catch (error) {
    status(messageOf(error));
  }
}

next.addEventListener("click", () => void show(cursor));
document.getElementById("start-over")!.addEventListener("click", () => void show(undefined));
void show(undefined);
