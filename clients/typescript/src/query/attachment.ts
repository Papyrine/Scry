// A handle to an attachment's bytes, made from the key a row came back with. It holds only the source,
// the member and the key, so it outlives the response it came from, and nothing about keeping one is
// privileged: every fetch is a request of its own, authorized afresh.

import { AttachmentRequest, type AttachmentKey } from "../wire.ts";
import type * as scry from "../runtime/types.ts";
import { ScryRequestError } from "../errors.ts";
import { serializeAttachment } from "../request/json.ts";
import type { AttachmentAnswer, ClientState } from "./query.ts";

export class AttachmentHandle implements scry.Attachment {
  readonly source: string;
  readonly member: string;
  readonly #keys: readonly AttachmentKey[];
  readonly #client: ClientState;

  constructor(client: ClientState, source: string, member: string, keys: readonly AttachmentKey[]) {
    this.#client = client;
    this.source = source;
    this.member = member;
    this.#keys = keys;
  }

  async open(options?: { readonly signal?: AbortSignal }): Promise<scry.AttachmentContent | null> {
    const client = this.#client;
    const transport = client.options.transport;
    if (transport?.attachment === undefined) {
      throw new Error("This client's transport does not fetch attachments: httpTransport() does.");
    }

    const request: AttachmentRequest = {
      version: AttachmentRequest.CurrentVersion,
      root: this.source,
      member: this.member,
      keys: [...this.#keys],
      ...(client.options.stamp === false ? {} : { stamp: client.schema.stamp })
    };
    let answer: AttachmentAnswer;
    try {
      answer = await transport.attachment(serializeAttachment(request), options?.signal);
    } catch (error) {
      // A fetch that stopped working is where a client most wants to hear its model drifted.
      if (error instanceof ScryRequestError) {
        client.observe(error.serverStamp);
      }

      throw error;
    }

    client.observe(answer.stamp);
    if (answer.content === undefined) {
      return null;
    }

    return content(answer.content.contentType, answer.content.length, answer.content.body);
  }
}

function content(contentType: string, length: number | undefined, body: ReadableStream<Uint8Array>): scry.AttachmentContent {
  return {
    contentType,
    length,
    body,
    async bytes() {
      return new Uint8Array(await new Response(body).arrayBuffer());
    },
    async blob() {
      return new Blob([await this.bytes() as Uint8Array<ArrayBuffer>], { type: contentType });
    }
  };
}
