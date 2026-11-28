// One live query, consumed one of two ways: handed to a callback, or read in a loop. Either way each
// answer is the whole current result, and a reprice from this page, another tab, or the console
// sample arrives the same way, with no reload and no polling from the page.

import { button, cell, commands, messageOf, post, scry, status } from "./client.ts";

type OrderRow = { id: number; region: string; amount: string; repriceable: boolean };

let orders: OrderRow[] = [];
let answers = 0;

// Whether a row may be repriced is decided by the server, row by row, from the command's policy, and
// decided again on every answer.
// begin-snippet: typescriptWebLiveQuery
const live = scry.order
  .orderBy(_ => _.id)
  .select(_ => ({ id: _.id, region: _.region, amount: _.amount, repriceable: _.canRepriceOrder }))
  .live();
// end-snippet

// begin-snippet: typescriptWebLive
function subscribe(): void {
  live.subscribe(
    answer => show(answer),
    error => status(`The live query ended: ${messageOf(error)}`));
}
// end-snippet

// The next answer is not read until the loop comes back for it, so a page slow to draw one is never
// handed a queue of them. Leaving the loop, or the page, ends the subscription.
// begin-snippet: typescriptWebLiveLoop
async function read(): Promise<void> {
  try {
    for await (const answer of live) {
      show(answer);
    }
  } catch (error) {
    status(`The live query ended: ${messageOf(error)}`);
  }
}
// end-snippet

function show(answer: OrderRow[]): void {
  orders = answer;
  answers++;
  document.getElementById("answers")!.textContent = String(answers);
  render();
}

function render(): void {
  const body = document.querySelector("#orders tbody")!;
  body.replaceChildren(...orders.map(order => {
    const row = document.createElement("tr");
    row.dataset["id"] = String(order.id);
    for (const text of [String(order.id), order.region, order.amount]) {
      row.append(cell(text));
    }

    const actions = document.createElement("td");
    actions.append(button("Reprice", () => void reprice(order.id), !order.repriceable || !commands.can("repriceOrder")));
    row.append(actions);
    return row;
  }));
}

// A command, and its outcome. One the server decides within its sync window is final at once; a
// pending one's completion is the outcome when it lands. A refusal throws, and nothing ran.
// begin-snippet: typescriptWebCommand
async function reprice(id: number): Promise<void> {
  status(`Repricing order ${id}…`);
  try {
    const sent = await commands.send.repriceOrder({ id });
    const outcome = await sent.completion;
    if (outcome.status === "Completed") {
      status(`Order ${id} repriced.`);
      return;
    }

    status(`Order ${id} was not repriced: ${outcome.error}`);
  } catch (error) {
    status(`Order ${id} was refused: ${messageOf(error)}`);
  }
}
// end-snippet

// A write that is no command: the server's own endpoint updates every order in one statement, which
// no interceptor sees, and says what it wrote, so the live query answers again at once.
async function repriceInBulk(): Promise<void> {
  try {
    await post("/api/orders/reprice-bulk");
    status("Every order repriced.");
  } catch (error) {
    status(messageOf(error));
  }
}

const bulk = button("Reprice in bulk", () => void repriceInBulk());
bulk.id = "reprice-bulk";
document.getElementById("actions")!.append(bulk);

// Whether the Reprice buttons are enabled is the server's word, read once and again after a denial.
commands.onCapabilitiesChanged(render);
void commands.ready;

const looping = new URLSearchParams(location.search).get("via") === "loop";
for (const tab of document.querySelectorAll<HTMLAnchorElement>("#via a")) {
  if ((tab.dataset["via"] === "loop") === looping) {
    tab.setAttribute("aria-current", "page");
  }
}

if (looping) {
  void read();
} else {
  subscribe();
}
