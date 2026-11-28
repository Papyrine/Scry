// The browser sample in a real browser, against a running Sample.WebServer that SCRY_SERVER names:
// Vite's dev server serving the pages and forwarding /api to that server, and Edge or Chrome, as
// installed, driven headless by playwright-core, which downloads no browser of its own. Every page,
// as the Blazor sample's tests drive its pages, and any console error fails the page it came from.
// Skipped otherwise, since the TypeScript suite needs no server of its own.
//
// The tests run in order and leave the data as they found it: a hire is deleted again, a revoked
// grant given back, and a deactivated manager reactivated. Reprices are not undone, so nothing reads
// an amount as a fixed number.

import { after, before, test } from "node:test";
import assert from "node:assert/strict";
import { join } from "node:path";
import { chromium, type Browser, type Page } from "playwright-core";
import { createServer, type ViteDevServer } from "vite";

const server = process.env["SCRY_SERVER"];
const skip = server === undefined ? "Set SCRY_SERVER to a running Sample.WebServer to run these." : false;
const root = join(import.meta.dirname, "..");

// Edge on Windows, where it always is; Chrome elsewhere. BROWSER_CHANNEL names another.
function channel(): string {
  if (process.env["BROWSER_CHANNEL"] !== undefined) {
    return process.env["BROWSER_CHANNEL"];
  }

  if (process.platform === "win32") {
    return "msedge";
  }

  return "chrome";
}

let vite: ViteDevServer;
let browser: Browser;

before(async () => {
  if (skip !== false) {
    return;
  }

  vite = await createServer({ root, configFile: join(root, "vite.config.ts"), logLevel: "silent", server: { port: 0 } });
  await vite.listen();
  browser = await chromium.launch({ channel: channel(), headless: true });
});

after(async () => {
  await browser?.close();
  await vite?.close();
});

// A page of the sample in a tab of its own, with what its console reported as errors.
async function open(path: string, prepare?: (page: Page) => Promise<unknown>): Promise<{ page: Page; errors: string[]; [Symbol.asyncDispose](): Promise<void> }> {
  const page = await browser.newPage();
  const errors: string[] = [];
  page.on("console", message => {
    if (message.type() === "error") {
      errors.push(message.text());
    }
  });
  page.on("pageerror", error => errors.push(error.message));
  await prepare?.(page);
  await page.goto(new URL(path, vite.resolvedUrls!.local[0]!).href);
  return {
    page,
    errors,
    async [Symbol.asyncDispose]() {
      await page.close();
    }
  };
}

const texts = (page: Page, selector: string) => page.locator(selector).allTextContents();

test("the queries page loads its tables and draws its photos", { skip, timeout: 120_000 }, async () => {
  await using tab = await open("/");
  const { page } = tab;

  await page.locator("#employees tbody tr").nth(2).waitFor();
  assert.deepEqual(await texts(page, "#employees tbody td:first-child"), ["Aaron", "Alice", "Carol"]);

  // The status and the count the page holds, sent as the request's constants.
  await page.locator("#full-timers tbody tr").nth(1).waitFor();
  assert.deepEqual(await texts(page, "#full-timers tbody td:first-child"), ["Aaron", "Alice"]);

  // A department projected as an object of its own.
  await page.locator("#cards tbody tr").nth(2).waitFor();
  assert.deepEqual(await texts(page, "#cards tbody td:nth-child(2)"), ["Engineering", "Engineering", "Sales"]);

  await page.locator("#regions tbody tr").first().waitFor();
  assert.deepEqual(await texts(page, "#regions tbody td:first-child"), ["North", "South"]);

  // Every employee with a photo has it drawn from the bytes its handle fetched; one has none.
  await page.locator("#photos figure").nth(3).waitFor();
  const drawn = await page.locator("#photos img").evaluateAll(images =>
    images.map(_ => ({ alt: (_ as HTMLImageElement).alt, blob: (_ as HTMLImageElement).src.startsWith("blob:"), loaded: (_ as HTMLImageElement).complete && (_ as HTMLImageElement).naturalWidth > 0 })));
  assert.deepEqual(drawn.map(_ => _.alt).sort(), ["Aaron", "Alice", "Bob"]);
  assert.ok(drawn.every(_ => _.blob && _.loaded), JSON.stringify(drawn));
  assert.equal(await page.locator("#photos .none").count(), 1);
  assert.equal(await page.locator("#stale").count(), 0);

  assert.deepEqual(tab.errors, []);
});

test("a server on another model is offered as a reload, and the queries still answer", { skip, timeout: 120_000 }, async () => {
  // Every answer said to come from a server on another model, as one redeployed since the page was
  // built would say.
  await using tab = await open("/", page => page.route("**/api/query**", async route => {
    const response = await route.fetch();
    await route.fulfill({ response, headers: { ...response.headers(), "scry-schema-stamp": "redeployed" } });
  }));
  const { page } = tab;

  await page.locator("#stale").waitFor();
  assert.match((await page.locator("#stale").textContent())!, /updated since this page was built/);
  await page.locator("#employees tbody tr").nth(2).waitFor();
  assert.equal(await page.locator("#stale").count(), 1);

  assert.deepEqual(tab.errors, []);
});

test("offset paging moves a page at a time, and says where there is no more", { skip, timeout: 120_000 }, async () => {
  await using tab = await open("/paging.html");
  const { page } = tab;
  const names = () => texts(page, "#employees tbody td:first-child");

  await page.getByText("Page 1").waitFor();
  assert.deepEqual(await names(), ["Aaron", "Alice"]);
  assert.equal(await page.locator("#previous").isDisabled(), true);

  await page.locator("#next").click();
  await page.getByText("Page 2").waitFor();
  assert.deepEqual(await names(), ["Bob", "Carol"]);
  assert.equal(await page.locator("#next").isDisabled(), true);

  await page.locator("#previous").click();
  await page.getByText("Page 1").waitFor();
  assert.deepEqual(await names(), ["Aaron", "Alice"]);

  assert.deepEqual(tab.errors, []);
});

test("keyset paging resumes from the cursor, and starts over", { skip, timeout: 120_000 }, async () => {
  await using tab = await open("/keyset.html");
  const { page } = tab;
  const names = () => texts(page, "#employees tbody td:first-child");

  await page.waitForFunction(() => !document.querySelector<HTMLButtonElement>("#next")!.disabled);
  assert.deepEqual(await names(), ["Aaron", "Alice"]);

  await page.locator("#next").click();
  await page.waitForFunction(() => document.querySelector<HTMLButtonElement>("#next")!.disabled);
  assert.deepEqual(await names(), ["Bob", "Carol"]);

  await page.locator("#start-over").click();
  await page.waitForFunction(() => !document.querySelector<HTMLButtonElement>("#next")!.disabled);
  assert.deepEqual(await names(), ["Aaron", "Alice"]);

  assert.deepEqual(tab.errors, []);
});

test("a cached row policy decides a row once, one revised row again, and every row after a grant moves", { skip, timeout: 120_000 }, async () => {
  const noCache: (string | undefined)[] = [];
  await using tab = await open("/permissions.html", async page => {
    page.on("request", request => {
      if (new URL(request.url()).pathname === "/api/query") {
        noCache.push(request.headers()["cache-control"]);
      }
    });
  });
  const { page } = tab;
  const regions = () => texts(page, "#orders tbody td:first-child");
  const decisions = async () => Number(await page.locator("#decisions").textContent());

  // Each step posts, then asks again; the page counts its loads on the table once each is drawn.
  async function step(click: () => Promise<void>): Promise<number> {
    const before = await page.locator("#orders").evaluate(_ => _.dataset["loads"]);
    await click();
    await page.waitForFunction(seen => document.querySelector<HTMLElement>("#orders")!.dataset["loads"] !== seen, before);
    return decisions();
  }

  await page.waitForFunction(() => document.querySelector<HTMLElement>("#orders")!.dataset["loads"] === "1");
  assert.deepEqual(await regions(), ["North", "North", "South"]);
  const decided = await decisions();

  // Every row was decided already, so asking again decides none.
  assert.equal(await step(() => page.locator("#reload").click()), decided);
  assert.deepEqual(await regions(), ["North", "North", "South"]);

  // One row past the revision the caller was decided up to: that row alone.
  assert.equal(await step(() => page.locator("#revise").click()), decided + 1);

  // A grant moved, which no row records: every row decided again, and South's gone.
  assert.equal(await step(() => page.locator("#grant-South").click()), decided + 4);
  assert.deepEqual(await regions(), ["North", "North"]);

  await step(() => page.locator("#grant-South").click());
  assert.deepEqual(await regions(), ["North", "North", "South"]);

  // The page asked afresh every time, past the browser's cache and the server's 304.
  assert.ok(noCache.length >= 5);
  assert.ok(noCache.every(_ => _ === "no-cache"), JSON.stringify(noCache));
  assert.deepEqual(tab.errors, []);
});

// The amount in a row of the live table, once it shows one.
async function amount(page: Page, id: number): Promise<number> {
  const cell = page.locator(`#orders tbody tr[data-id='${id}'] td:nth-child(3)`);
  await cell.waitFor();
  return Number(await cell.textContent());
}

async function shows(page: Page, id: number, value: number): Promise<void> {
  await page.waitForFunction(
    ([row, expected]) => document.querySelector(`#orders tbody tr[data-id='${row}'] td:nth-child(3)`)?.textContent === expected,
    [id, value.toFixed(2)] as const);
}

test("a live query handed to a callback shows a reprice as it lands", { skip, timeout: 120_000 }, async () => {
  await using tab = await open("/live.html");
  const { page } = tab;

  const before = await amount(page, 2);
  await page.waitForFunction(() => !document.querySelector<HTMLButtonElement>("#orders tbody tr[data-id='2'] button")!.disabled);
  await page.locator("#orders tbody tr[data-id='2'] button").click();
  await shows(page, 2, before + 1);
  await page.getByText("Order 2 repriced.").waitFor();

  assert.deepEqual(tab.errors, []);
});

test("a live query read in a loop shows a write no command made", { skip, timeout: 120_000 }, async () => {
  await using tab = await open("/live.html?via=loop");
  const { page } = tab;

  assert.equal(await page.locator("#via a[aria-current='page']").textContent(), "Loop");
  const before = await amount(page, 3);
  const answers = Number(await page.locator("#answers").textContent());
  await page.locator("#reprice-bulk").click();
  await shows(page, 3, before + 1);
  assert.ok(Number(await page.locator("#answers").textContent()) > answers);

  assert.deepEqual(tab.errors, []);
});

test("commands write what the live table then shows, and a slow one waits in pending work", { skip, timeout: 120_000 }, async () => {
  await using tab = await open("/commands.html");
  const { page } = tab;
  const row = (name: string) => page.locator("#employees tbody tr").filter({ has: page.locator(".name", { hasText: new RegExp(`^${name}$`) }) });
  const status = page.locator("#status");

  await page.locator("#employees[data-ready='true']").waitFor();
  assert.equal(await page.locator("#create-submit").isDisabled(), false);

  // Only an inactive employee may be deleted.
  const deletable = await page.locator("#employees tbody tr").evaluateAll(rows =>
    rows.filter(_ => !_.querySelector<HTMLButtonElement>(".delete")!.disabled).map(_ => _.querySelector(".name")!.textContent));
  assert.deepEqual(deletable, ["Bob"]);

  // A hire answers with the new row's id, and the row arrives as the next answer.
  const hire = `Browser hire ${Date.now()}`;
  await page.locator("#create-name").fill(hire);
  await page.locator("#create-submit").click();
  await page.getByText(new RegExp(`^Hired ${hire} as #\\d+\\.$`)).waitFor();
  await row(hire).waitFor();

  // A rename to a slow name goes pending, waits in the panel, and lands.
  const slow = `${hire} slow`;
  await page.locator("#rename-to").fill(slow);
  await row(hire).locator(".rename").click();
  await page.getByText("RenameEmployee is taking a while. It is in pending work.").waitFor();
  await page.locator("#pending li[data-status='pending']").waitFor();
  await row(slow).waitFor({ timeout: 30_000 });
  await page.locator("#pending li[data-status='completed']").waitFor();

  // Deactivating enables Delete as the next answer arrives, and deleting removes the row.
  await row(slow).locator(".toggle").click();
  await page.waitForFunction(name => [...document.querySelectorAll("#employees tbody tr")]
    .some(_ => _.querySelector(".name")!.textContent === name && !_.querySelector<HTMLButtonElement>(".delete")!.disabled), slow);
  await row(slow).locator(".delete").click();
  await page.getByText(`Deleted ${slow}.`).waitFor();
  await row(slow).waitFor({ state: "detached" });

  // A manager cannot be deleted, inactive or not: the handler says why, and the row stays.
  await row("Alice").locator(".toggle").click();
  await page.waitForFunction(() => [...document.querySelectorAll("#employees tbody tr")]
    .some(_ => _.querySelector(".name")!.textContent === "Alice" && !_.querySelector<HTMLButtonElement>(".delete")!.disabled));
  await row("Alice").locator(".delete").click();
  await page.waitForFunction(() => document.querySelector("#status")!.textContent!.includes("manages others"));
  assert.equal(await row("Alice").count(), 1);
  await row("Alice").locator(".toggle").click();
  await page.waitForFunction(() => [...document.querySelectorAll("#employees tbody tr")]
    .some(_ => _.querySelector(".name")!.textContent === "Alice" && _.querySelector(".active")!.textContent === "yes"));
  assert.ok((await status.textContent())!.startsWith("Reactivated Alice"));

  assert.deepEqual(tab.errors, []);
});
