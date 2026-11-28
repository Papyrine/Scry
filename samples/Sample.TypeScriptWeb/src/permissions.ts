// A row policy whose decision is too slow to make per row in SQL: the server decides each row in C#
// once and remembers the answers, and the query carries only a membership test over the rows this
// caller may see. The counter is what that saves: it moves only when a decision is made.

import { button, fill, messageOf, post, scry, status } from "./client.ts";

interface Grants {
  readonly regions: readonly string[];
  readonly granted: readonly string[];
  readonly lookups: number;
}

let first: number | undefined;

// Asked afresh rather than answered from the browser's cache. Everywhere else a repeated query is a
// 304, which is the point of one, but a 304 is the server deciding nothing, and deciding is what this
// page shows. The header is this query's alone and never part of the request, which stays the same
// bytes.
// begin-snippet: typescriptWebNoCache
async function showOrders(): Promise<void> {
  const orders = await scry.order
    .withHeader("Cache-Control", "no-cache")
    .orderBy(_ => _.region)
    .thenBy(_ => _.amount)
    .select(_ => ({ id: _.id, region: _.region, amount: _.amount }))
    .toList();

  first = orders[0]?.id;
  fill("orders", orders.map(_ => [_.region, _.amount]));
}
// end-snippet

// The sample's own record of the grants and of how many decisions the policy has made: nothing of
// Scry's, which never says whether it decided.
async function showGrants(): Promise<void> {
  const response = await fetch("/api/grants");
  const grants = await response.json() as Grants;
  document.getElementById("decisions")!.textContent = String(grants.lookups);
  document.getElementById("grants")!.replaceChildren(...grants.regions.map(region => {
    const item = document.createElement("li");
    const label = document.createElement("label");
    const box = document.createElement("input");
    box.type = "checkbox";
    box.id = `grant-${region}`;
    box.checked = grants.granted.includes(region);
    box.addEventListener("change", () => void act(() => post(`/api/grants/${region}?allowed=${box.checked}`)));
    label.append(box, ` ${region}`);
    item.append(label);
    return item;
  }));
}

// Counted on the table, so whatever watches the page can tell one load from the next.
async function load(): Promise<void> {
  await showOrders();
  await showGrants();
  revise.disabled = first === undefined;
  const table = document.getElementById("orders")!;
  table.dataset["loads"] = String(Number(table.dataset["loads"] ?? "0") + 1);
}

// Something written, then the page asked again.
async function act(write: () => Promise<void>): Promise<void> {
  status("");
  try {
    await write();
    await load();
  } catch (error) {
    status(messageOf(error));
  }
}

// Moves one order past the revision the caller was decided up to, so the next query decides it alone.
const revise = button("Revise an order", () => void act(() => post(`/api/orders/${first}/touch`)), true);
revise.id = "revise";
const reload = button("Run the query again", () => void act(async () => {}));
reload.id = "reload";
document.getElementById("actions")!.append(reload, " ", revise);

void act(async () => {});
