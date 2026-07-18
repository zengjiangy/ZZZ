import { describe, expect, it } from "vitest";
import { handleRequest, type Env } from "./index";

const KEY = "releases/v2.1.0/ZZZ-v2.1.0-win-x64.exe";
const DATA = new TextEncoder().encode("0123456789");

function metadata(key: string, data: Uint8Array): R2Object {
  return {
    key,
    version: "test-version",
    size: data.byteLength,
    etag: "test-etag",
    httpEtag: '"test-etag"',
    uploaded: new Date("2026-07-16T00:00:00Z"),
    httpMetadata: {},
    customMetadata: {},
    range: undefined,
    checksums: {} as R2Checksums,
    storageClass: "Standard",
    writeHttpMetadata: () => undefined,
  } as R2Object;
}

class FakeBucket {
  readonly objects = new Map<string, Uint8Array>([[KEY, DATA]]);
  headCalls: string[] = [];
  getCalls: string[] = [];

  async head(key: string): Promise<R2Object | null> {
    this.headCalls.push(key);
    const data = this.objects.get(key);
    return data ? metadata(key, data) : null;
  }

  async get(key: string, options?: R2GetOptions): Promise<R2ObjectBody | null> {
    this.getCalls.push(key);
    const data = this.objects.get(key);
    if (!data) {
      return null;
    }

    let result = data;
    const range = options?.range;
    if (range && !(range instanceof Headers) && "offset" in range) {
      const offset = range.offset ?? 0;
      const length = range.length ?? data.byteLength - offset;
      result = data.slice(offset, offset + length);
    }

    return {
      ...metadata(key, data),
      body: new Response(result).body!,
      bodyUsed: false,
      arrayBuffer: async () => result.buffer.slice(result.byteOffset, result.byteOffset + result.byteLength),
      text: async () => new TextDecoder().decode(result),
      json: async () => JSON.parse(new TextDecoder().decode(result)) as unknown,
      blob: async () => new Blob([result]),
    } as R2ObjectBody;
  }
}

function testEnv(bucket = new FakeBucket()): { env: Env; bucket: FakeBucket } {
  return {
    env: { RELEASES: bucket as unknown as R2Bucket },
    bucket,
  };
}

describe("download worker", () => {
  it("returns JSON health information at the root", async () => {
    const { env } = testEnv();
    const response = await handleRequest(new Request("https://download.zzz.campusphere.ltd/"), env);

    expect(response.status).toBe(200);
    expect(response.headers.get("access-control-allow-origin")).toBe("*");
    expect(await response.json()).toMatchObject({ status: "ok", latest: "v2.1.0" });
  });

  it("returns a bodyless HEAD response with GET-equivalent metadata", async () => {
    const { env, bucket } = testEnv();
    const response = await handleRequest(
      new Request(`https://download.zzz.campusphere.ltd/${KEY}`, { method: "HEAD" }),
      env,
    );

    expect(response.status).toBe(200);
    expect(response.body).toBeNull();
    expect(response.headers.get("content-length")).toBe(String(DATA.byteLength));
    expect(response.headers.get("content-disposition")).toContain("attachment");
    expect(bucket.headCalls).toEqual([KEY]);
    expect(bucket.getCalls).toEqual([]);
  });

  it.each(["x64", "x86", "arm64"])("redirects /latest/%s to v2.1.0", async (architecture) => {
    const { env } = testEnv();
    const response = await handleRequest(
      new Request(`https://download.zzz.campusphere.ltd/latest/${architecture}`),
      env,
    );

    expect(response.status).toBe(302);
    expect(response.headers.get("location")).toMatch(/^\/releases\/v2\.1\.0\/ZZZ-v2\.1\.0-win-/);
  });

  it("streams a release asset with immutable attachment headers", async () => {
    const { env, bucket } = testEnv();
    const response = await handleRequest(
      new Request(`https://download.zzz.campusphere.ltd/${KEY}`),
      env,
    );

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toContain("immutable");
    expect(response.headers.get("content-disposition")).toContain("attachment");
    expect(await response.text()).toBe("0123456789");
    expect(bucket.getCalls).toEqual([KEY]);
  });

  it("supports a single byte range", async () => {
    const { env } = testEnv();
    const response = await handleRequest(
      new Request(`https://download.zzz.campusphere.ltd/${KEY}`, {
        headers: { Range: "bytes=2-5" },
      }),
      env,
    );

    expect(response.status).toBe(206);
    expect(response.headers.get("content-range")).toBe("bytes 2-5/10");
    expect(response.headers.get("content-length")).toBe("4");
    expect(await response.text()).toBe("2345");
  });

  it("returns 416 for an invalid or multiple range", async () => {
    const { env } = testEnv();
    const response = await handleRequest(
      new Request(`https://download.zzz.campusphere.ltd/${KEY}`, {
        headers: { Range: "bytes=0-1,4-5" },
      }),
      env,
    );

    expect(response.status).toBe(416);
    expect(response.headers.get("content-range")).toBe("bytes */10");
  });

  it("rejects unsafe paths before accessing R2", async () => {
    const { env, bucket } = testEnv();
    const response = await handleRequest(
      new Request("https://download.zzz.campusphere.ltd/releases/v2.1.0/%252e%252e%252fsecret.exe"),
      env,
    );

    expect(response.status).toBe(404);
    expect(bucket.headCalls).toEqual([]);
    expect(bucket.getCalls).toEqual([]);
  });

  it("returns 404 for a missing release object", async () => {
    const { env } = testEnv();
    const response = await handleRequest(
      new Request("https://download.zzz.campusphere.ltd/releases/v2.1.0/missing.exe"),
      env,
    );

    expect(response.status).toBe(404);
  });

  it("returns CORS preflight metadata", async () => {
    const { env } = testEnv();
    const response = await handleRequest(
      new Request("https://download.zzz.campusphere.ltd/latest/x64", { method: "OPTIONS" }),
      env,
    );

    expect(response.status).toBe(204);
    expect(response.headers.get("access-control-allow-methods")).toBe("GET, HEAD, OPTIONS");
  });

  it("returns 405 with an Allow header for write methods", async () => {
    const { env } = testEnv();
    const response = await handleRequest(
      new Request("https://download.zzz.campusphere.ltd/", { method: "POST" }),
      env,
    );

    expect(response.status).toBe(405);
    expect(response.headers.get("allow")).toBe("GET, HEAD, OPTIONS");
  });
});
