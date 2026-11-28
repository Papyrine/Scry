// Offset paging: skip to the start of the page, then one page of rows, whose hasMore enables Next.

import { fill, messageOf, scry, status } from "./client.ts";

// Small, so the four seeded employees span two pages.
const pageSize = 2;
let index = 0;

// begin-snippet: typescriptWebPaging
async function load(): Promise<void> {
  const page = await scry.employee
    .orderBy(_ => _.name)
    .skip(index * pageSize)
    .select(_ => ({ name: _.name, status: _.status, department: _.department.name }))
    .page(pageSize);

  fill("employees", page.items.map(_ => [_.name, _.status, _.department ?? ""]));
  previous.disabled = index === 0;
  next.disabled = !page.hasMore;
  document.getElementById("number")!.textContent = `Page ${index + 1}`;
}
// end-snippet

const previous = document.querySelector<HTMLButtonElement>("#previous")!;
const next = document.querySelector<HTMLButtonElement>("#next")!;

async function move(by: number): Promise<void> {
  index += by;
  try {
    await load();
  } catch (error) {
    status(messageOf(error));
  }
}

previous.addEventListener("click", () => void move(-1));
next.addEventListener("click", () => void move(1));
void move(0);
