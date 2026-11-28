// Commands against a live table. The page sends and never reads again: what a command writes comes
// back through the live query, for this page and every other. Only an inactive employee may be
// deleted, and a name containing "slow" takes a while, waiting in pending work until it lands.

import type { CommandOutcome } from "scry-client";
import { button, cell, commands, messageOf, scry, status } from "./client.ts";

type EmployeeRow = { id: number; name: string; active: boolean; deletable: boolean };

let rows: EmployeeRow[] = [];
let answered = false;
let capable = false;

// Each row's Delete is its canDeleteEmployee: the command's policy, decided per row in the database
// and again on every answer, so deactivating a row enables its Delete as the next answer arrives.
// begin-snippet: typescriptWebCommandsQuery
scry.employee
  .orderBy(_ => _.id)
  .select(_ => ({ id: _.id, name: _.name, active: _.active, deletable: _.canDeleteEmployee }))
  .live()
  .subscribe(
    answer => {
      rows = answer;
      answered = true;
      render();
    },
    error => status(`The live query ended: ${messageOf(error)}`));
// end-snippet

const hireName = document.querySelector<HTMLInputElement>("#create-name")!;
const hire = document.querySelector<HTMLButtonElement>("#create-submit")!;
const renameTo = document.querySelector<HTMLInputElement>("#rename-to")!;

function render(): void {
  // What this caller may send at all: false until the server has said, and said again whenever a
  // denial shows it has moved.
  hire.disabled = !commands.can("createEmployee");
  const renaming = commands.can("renameEmployee");
  document.querySelector("#employees tbody")!.replaceChildren(...rows.map(row => {
    const tr = document.createElement("tr");
    tr.dataset["id"] = String(row.id);
    const name = cell(row.name);
    name.className = "name";
    const active = cell(row.active ? "yes" : "no");
    active.className = "active";
    const actions = document.createElement("td");
    actions.className = "actions";
    const toggle = button(row.active ? "Deactivate" : "Reactivate", () => void setActive(row));
    toggle.className = "toggle";
    const rename = button("Rename", () => void renameRow(row), !renaming);
    rename.className = "rename";
    const remove = button("Delete", () => void deleteRow(row), !row.deletable);
    remove.className = "delete";
    actions.append(toggle, " ", rename, " ", remove);
    tr.append(cell(String(row.id)), name, active, actions);
    return tr;
  }));

  // What a test waits on before it clicks: the table and the capabilities both in.
  const table = document.getElementById("employees")!;
  if (answered &&
      capable) {
    table.dataset["ready"] = "true";
  }
}

// begin-snippet: typescriptWebCommandsSend
function deleteRow(row: EmployeeRow): Promise<void> {
  return send(() => commands.send.deleteEmployee({ id: row.id }), `Deleted ${row.name}.`);
}

function renameRow(row: EmployeeRow): Promise<void> {
  const name = renameTo.value;
  return send(() => commands.send.renameEmployee({ id: row.id, name }), `Renamed ${row.name} to ${name}.`);
}

function setActive(row: EmployeeRow): Promise<void> {
  let done = `Reactivated ${row.name}.`;
  if (row.active) {
    done = `Deactivated ${row.name}.`;
  }

  return send(() => commands.send.setEmployeeActive({ id: row.id, active: !row.active }), done);
}

// The typed outcome: the new row's id, read off the result the handler answered with.
async function create(): Promise<void> {
  const name = hireName.value;
  status("");
  try {
    const hired = await commands.send.createEmployee({ name, departmentId: 1, status: "FullTime" });
    if (hired.status === "Completed") {
      status(`Hired ${name} as #${hired.result.id}.`);
      return;
    }

    told(hired, undefined);
  } catch (error) {
    status(messageOf(error));
  }
}

async function send(sending: () => Promise<CommandOutcome<unknown>>, done: string): Promise<void> {
  status("");
  try {
    told(await sending(), done);
  } catch (error) {
    // Refused before it ran (malformed, denied, one too many), so nothing was written.
    status(messageOf(error));
  }
}

function told(outcome: CommandOutcome<unknown>, done: string | undefined): void {
  if (outcome.status === "Completed") {
    status(done ?? "");
    return;
  }

  // Still running when the client stopped waiting: pending work follows it to its end, and the table
  // shows what it wrote when it lands.
  if (outcome.status === "Pending") {
    status(`${outcome.command} is taking a while. It is in pending work.`);
    return;
  }

  status(outcome.error);
}
// end-snippet

// Every command that went pending, from then until its outcome has been seen: a completed one leaves
// on its own after a moment, and a failed or unknown one stays until cleared.
// begin-snippet: typescriptWebPendingWork
function renderPending(): void {
  const list = document.getElementById("pending")!;
  list.replaceChildren(...commands.pending.map(entry => {
    const item = document.createElement("li");
    item.dataset["status"] = entry.status.toLowerCase();
    item.textContent = `${entry.command} ${entry.keys.join(", ")}: ${entry.status.toLowerCase()}`;
    if (entry.error !== undefined) {
      item.textContent += `, ${entry.error}`;
    }

    return item;
  }));
  document.getElementById("pending-work")!.hidden = commands.pending.length === 0;
}

commands.onPendingChanged(renderPending);
// end-snippet

document.getElementById("clear-finished")!.addEventListener("click", () => commands.clearFinished());
document.getElementById("create")!.addEventListener("submit", event => {
  event.preventDefault();
  void create();
});

commands.onCapabilitiesChanged(render);
void commands.ready.then(() => {
  capable = true;
  render();
});
renderPending();
