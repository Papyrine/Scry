// The browser sample in a real browser, against a running Sample.WebServer that SCRY_SERVER names:
// Vite's dev server serving the page and forwarding /api to that server, and Edge or Chrome, as
// installed, driven headless by playwright-core, which downloads no browser of its own. The tables
// load, the photos are drawn from attachment handles, and a reprice reaches the live table. Skipped
// otherwise, since the TypeScript suite needs no server of its own.

import { test } from "node:test";
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

async function open(): Promise<{ vite: ViteDevServer; browser: Browser; page: Page; errors: string[] }> {
  const vite = await createServer({ root, configFile: join(root, "vite.config.ts"), logLevel: "silent", server: { port: 0 } });
  await vite.listen();
  const browser = await chromium.launch({ channel: channel(), headless: true });
  const page = await browser.newPage();
  const errors: string[] = [];
  page.on("console", message => {
    if (message.type() === "error") {
      errors.push(message.text());
    }
  });
  page.on("pageerror", error => errors.push(error.message));
  await page.goto(vite.resolvedUrls!.local[0]!);
  return { vite, browser, page, errors };
}

test("the page loads its tables, draws its photos, and shows a reprice as it lands", { skip, timeout: 120_000 }, async () => {
  const { vite, browser, page, errors } = await open();
  try {
    const employees = page.locator("#employees tbody tr");
    await employees.nth(2).waitFor();
    assert.deepEqual(await employees.locator("td:first-child").allTextContents(), ["Aaron", "Alice", "Carol"]);

    await page.locator("#regions tbody tr").first().waitFor();
    assert.deepEqual(await page.locator("#regions tbody td:first-child").allTextContents(), ["North", "South"]);

    // Every employee with a photo has it drawn from the bytes its handle fetched; one has none.
    await page.locator("#photos figure").nth(3).waitFor();
    const drawn = await page.locator("#photos img").evaluateAll(images =>
      images.map(_ => ({ alt: (_ as HTMLImageElement).alt, blob: (_ as HTMLImageElement).src.startsWith("blob:"), loaded: (_ as HTMLImageElement).complete && (_ as HTMLImageElement).naturalWidth > 0 })));
    assert.deepEqual(drawn.map(_ => _.alt).sort(), ["Aaron", "Alice", "Bob"]);
    assert.ok(drawn.every(_ => _.blob && _.loaded), JSON.stringify(drawn));
    assert.equal(await page.locator("#photos .none").count(), 1);

    // A command, and the live query that reads the row it wrote.
    const order = page.locator("#orders tbody tr[data-id='2']");
    await order.waitFor();
    const before = Number(await order.locator("td").nth(2).textContent());
    const reprice = order.locator("button");
    await page.waitForFunction(() => !document.querySelector<HTMLButtonElement>("#orders tbody tr[data-id='2'] button")!.disabled);
    await reprice.click();
    await page.waitForFunction(
      expected => document.querySelector("#orders tbody tr[data-id='2'] td:nth-child(3)")?.textContent === expected,
      (before + 1).toFixed(2));
    await page.getByText("Order 2 repriced.").waitFor();

    assert.deepEqual(errors, []);
  } finally {
    await browser.close();
    await vite.close();
  }
});
