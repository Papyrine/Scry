// The queries page: the console sample's queries, a filter on values the page holds, a projection
// that nests, and the photos drawn from the attachment handles a query brought back.

import { eq, ScryStaleClientError } from "scry-client";
import { fill, messageOf, scry, status } from "./client.ts";

async function showEmployees(): Promise<void> {
  const rows = await scry.employee
    .where(_ => _.active)
    .orderBy(_ => _.name)
    .select(_ => ({ name: _.name, status: _.status, manager: _.manager.name, department: _.department.name }))
    .toList();

  fill("employees", rows.map(_ => [_.name, _.status, _.manager ?? "", _.department ?? ""]));
}

// Values the page holds travel as the request's constants, so the query is written once and asked
// with whatever they are.
// begin-snippet: typescriptWebValues
const wanted = "FullTime";
const top = 2;

async function showFullTimers(): Promise<void> {
  const rows = await scry.employee
    .where(_ => eq(_.status, wanted))
    .orderBy(_ => _.name)
    .take(top)
    .select(_ => ({ name: _.name, department: _.department.name }))
    .toList();

  fill("full-timers", rows.map(_ => [_.name, _.department ?? ""]));
}
// end-snippet

// A projection into the department builds an object of its own rather than a column beside the name.
// begin-snippet: typescriptWebNested
async function showCards(): Promise<void> {
  const cards = await scry.employee
    .where(_ => _.active)
    .orderBy(_ => _.name)
    .select(_ => ({ name: _.name, department: { name: _.department.name } }))
    .toList();
  // { name: string; department: { name: string | null } }[]

  fill("cards", cards.map(_ => [_.name, _.department.name ?? ""]));
}
// end-snippet

// Each handle fetches its bytes as a request of its own, and a blob of them is what an image shows.
// begin-snippet: typescriptWebPhotos
async function showPhotos(): Promise<void> {
  const employees = await scry.employee
    .orderBy(_ => _.id)
    .select(_ => ({ id: _.id, name: _.name, photo: _.photo }))
    .toList();

  const photos = document.getElementById("photos")!;
  for (const employee of employees) {
    const figure = document.createElement("figure");
    const content = await employee.photo.open();
    if (content === null) {
      const none = document.createElement("div");
      none.className = "none";
      none.textContent = "no photo";
      figure.append(none);
    } else {
      const image = document.createElement("img");
      image.src = URL.createObjectURL(await content.blob());
      image.alt = employee.name;
      figure.append(image);
    }

    const caption = document.createElement("figcaption");
    caption.textContent = employee.name;
    figure.append(caption);
    photos.append(figure);
  }
}
// end-snippet

async function showRegions(): Promise<void> {
  const rows = await scry.order
    .groupBy(_ => _.region)
    .select(group => ({ region: group.key, total: group.sum(_ => _.amount), count: group.count() }))
    .toList();

  fill("regions", rows.map(_ => [_.region, _.total, String(_.count)]));
}

// A query this page's model can no longer ask is refused as a stale client. The banner offering a
// reload is already showing, from the same response, so what could not load says so rather than
// reading as the page failing.
// begin-snippet: typescriptWebStaleQuery
async function show(query: () => Promise<void>): Promise<void> {
  try {
    await query();
  } catch (error) {
    if (error instanceof ScryStaleClientError) {
      status("This page was built for an older version of the server: reload it to see this.");
      return;
    }

    status(messageOf(error));
  }
}
// end-snippet

for (const query of [showEmployees, showFullTimers, showCards, showPhotos, showRegions]) {
  void show(query);
}
