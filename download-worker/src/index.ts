export interface Env {
  RELEASES: R2Bucket;
}

type Architecture = "x64" | "x86" | "arm64";

interface ReleaseAsset {
  key: string;
  filename: string;
}

interface ByteRange {
  offset: number;
  length: number;
  start: number;
  end: number;
}

const SERVICE_NAME = "zzz-release-downloads";
const LATEST_VERSION = "v2.2.1";
const ALLOWED_METHODS = "GET, HEAD, OPTIONS";
const RELEASE_CACHE_CONTROL = "public, max-age=31536000, immutable";
const LATEST_CACHE_CONTROL = "public, max-age=300, must-revalidate";

const LATEST_ASSETS: Readonly<Record<Architecture, string>> = {
  x64: "ZZZ-v2.2.1-win-x64.exe",
  x86: "ZZZ-v2.2.1-win-x86.exe",
  arm64: "ZZZ-v2.2.1-win-arm64.exe",
};

const VERSION_PATTERN = /^v\d+(?:\.\d+){1,2}(?:[-+][0-9A-Za-z.-]+)?$/;
const EXE_FILENAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,191}\.exe$/i;

function applyCors(headers: Headers): Headers {
  headers.set("Access-Control-Allow-Origin", "*");
  headers.set("Access-Control-Expose-Headers", "Accept-Ranges, Content-Disposition, Content-Length, Content-Range, ETag, Last-Modified, Location");
  headers.set("Cross-Origin-Resource-Policy", "cross-origin");
  return headers;
}

function applySecurityHeaders(headers: Headers): Headers {
  headers.set("X-Content-Type-Options", "nosniff");
  headers.set("Referrer-Policy", "no-referrer");
  return headers;
}

function jsonResponse(
  payload: unknown,
  status: number,
  method: string,
  extraHeaders?: HeadersInit,
): Response {
  const body = `${JSON.stringify(payload, null, 2)}\n`;
  const headers = new Headers(extraHeaders);
  headers.set("Content-Type", "application/json; charset=utf-8");
  headers.set("Content-Length", String(new TextEncoder().encode(body).byteLength));
  headers.set("Cache-Control", "no-store");
  applyCors(headers);
  applySecurityHeaders(headers);

  return new Response(method === "HEAD" ? null : body, {
    status,
    headers,
  });
}

function notFound(method: string): Response {
  return jsonResponse(
    {
      error: "not_found",
      message: "The requested download endpoint or release asset was not found.",
    },
    404,
    method,
  );
}

function methodNotAllowed(method: string): Response {
  return jsonResponse(
    {
      error: "method_not_allowed",
      message: `Only ${ALLOWED_METHODS} are supported.`,
    },
    405,
    method,
    { Allow: ALLOWED_METHODS },
  );
}

function optionsResponse(): Response {
  const headers = new Headers({
    Allow: ALLOWED_METHODS,
    "Access-Control-Allow-Headers": "Range, If-Match, If-None-Match, If-Modified-Since, If-Unmodified-Since",
    "Access-Control-Allow-Methods": ALLOWED_METHODS,
    "Access-Control-Max-Age": "86400",
    "Cache-Control": "public, max-age=86400",
  });
  applyCors(headers);
  applySecurityHeaders(headers);

  return new Response(null, { status: 204, headers });
}

function decodePathname(pathname: string): string | null {
  try {
    const decoded = decodeURIComponent(pathname);
    if (decoded.includes("\0") || decoded.includes("\\") || decoded.includes("//")) {
      return null;
    }
    return decoded;
  } catch {
    return null;
  }
}

function parseReleaseAsset(pathname: string): ReleaseAsset | null {
  const segments = pathname.split("/");
  if (
    segments.length !== 4 ||
    segments[0] !== "" ||
    segments[1] !== "releases"
  ) {
    return null;
  }

  const version = segments[2];
  const filename = segments[3];
  if (!VERSION_PATTERN.test(version) || !EXE_FILENAME_PATTERN.test(filename)) {
    return null;
  }

  return {
    key: `releases/${version}/${filename}`,
    filename,
  };
}

function parseRange(rangeHeader: string, size: number): ByteRange | null {
  if (size <= 0) {
    return null;
  }

  const match = /^bytes=(\d*)-(\d*)$/.exec(rangeHeader.trim());
  if (!match || (match[1] === "" && match[2] === "")) {
    return null;
  }

  const startText = match[1];
  const endText = match[2];

  if (startText === "") {
    const suffixLength = Number(endText);
    if (!Number.isSafeInteger(suffixLength) || suffixLength <= 0) {
      return null;
    }
    const length = Math.min(suffixLength, size);
    const start = size - length;
    return { offset: start, length, start, end: size - 1 };
  }

  const start = Number(startText);
  if (!Number.isSafeInteger(start) || start < 0 || start >= size) {
    return null;
  }

  let end = size - 1;
  if (endText !== "") {
    const requestedEnd = Number(endText);
    if (!Number.isSafeInteger(requestedEnd) || requestedEnd < start) {
      return null;
    }
    end = Math.min(requestedEnd, size - 1);
  }

  return {
    offset: start,
    length: end - start + 1,
    start,
    end,
  };
}

function downloadHeaders(
  object: R2Object,
  filename: string,
  range?: ByteRange,
): Headers {
  const headers = new Headers({
    "Accept-Ranges": "bytes",
    "Cache-Control": RELEASE_CACHE_CONTROL,
    "Content-Disposition": `attachment; filename="${filename}"; filename*=UTF-8''${encodeURIComponent(filename)}`,
    "Content-Length": String(range?.length ?? object.size),
    "Content-Type": "application/vnd.microsoft.portable-executable",
    ETag: object.httpEtag,
    "Last-Modified": object.uploaded.toUTCString(),
  });

  if (range) {
    headers.set("Content-Range", `bytes ${range.start}-${range.end}/${object.size}`);
  }

  applyCors(headers);
  applySecurityHeaders(headers);
  return headers;
}

function rangeNotSatisfiable(method: string, size: number): Response {
  return jsonResponse(
    {
      error: "range_not_satisfiable",
      message: "Only one valid byte range may be requested at a time.",
    },
    416,
    method,
    {
      "Accept-Ranges": "bytes",
      "Content-Range": `bytes */${size}`,
    },
  );
}

async function serveReleaseAsset(
  request: Request,
  env: Env,
  asset: ReleaseAsset,
): Promise<Response> {
  const rangeHeader = request.headers.get("Range");

  if (request.method === "HEAD" || rangeHeader !== null) {
    const metadata = await env.RELEASES.head(asset.key);
    if (metadata === null) {
      return notFound(request.method);
    }

    const parsedRange = rangeHeader === null ? undefined : parseRange(rangeHeader, metadata.size);
    if (rangeHeader !== null && parsedRange === null) {
      return rangeNotSatisfiable(request.method, metadata.size);
    }
    const range = parsedRange ?? undefined;

    const status = range ? 206 : 200;
    const headers = downloadHeaders(metadata, asset.filename, range);

    if (request.method === "HEAD") {
      return new Response(null, { status, headers });
    }

    const object = await env.RELEASES.get(asset.key, {
      range: { offset: range!.offset, length: range!.length },
    });
    if (object === null) {
      return notFound(request.method);
    }
    if (!("body" in object)) {
      return jsonResponse(
        { error: "precondition_failed", message: "The release asset changed while it was being read." },
        412,
        request.method,
      );
    }

    return new Response(object.body, { status: 206, headers });
  }

  const object = await env.RELEASES.get(asset.key);
  if (object === null) {
    return notFound(request.method);
  }
  if (!("body" in object)) {
    return jsonResponse(
      { error: "precondition_failed", message: "The release asset could not be read." },
      412,
      request.method,
    );
  }

  return new Response(object.body, {
    status: 200,
    headers: downloadHeaders(object, asset.filename),
  });
}

function healthResponse(method: string): Response {
  return jsonResponse(
    {
      status: "ok",
      service: SERVICE_NAME,
      latest: LATEST_VERSION,
      endpoints: {
        x64: "/latest/x64",
        x86: "/latest/x86",
        arm64: "/latest/arm64",
        releases: "/releases/{version}/{filename.exe}",
      },
      repository: "https://github.com/zengjiangy/ZZZ",
    },
    200,
    method,
  );
}

function latestRedirect(architecture: Architecture): Response {
  const filename = LATEST_ASSETS[architecture];
  const location = `/releases/${LATEST_VERSION}/${filename}`;
  const headers = new Headers({
    "Cache-Control": LATEST_CACHE_CONTROL,
    "Content-Length": "0",
    Location: location,
  });
  applyCors(headers);
  applySecurityHeaders(headers);
  return new Response(null, { status: 302, headers });
}

export async function handleRequest(request: Request, env: Env): Promise<Response> {
  if (request.method === "OPTIONS") {
    return optionsResponse();
  }

  if (request.method !== "GET" && request.method !== "HEAD") {
    return methodNotAllowed(request.method);
  }

  const url = new URL(request.url);
  const pathname = decodePathname(url.pathname);
  if (pathname === null) {
    return notFound(request.method);
  }

  try {
    if (pathname === "/") {
      return healthResponse(request.method);
    }

    const latestMatch = /^\/latest\/(x64|x86|arm64)$/.exec(pathname);
    if (latestMatch) {
      return latestRedirect(latestMatch[1] as Architecture);
    }

    const asset = parseReleaseAsset(pathname);
    if (asset) {
      return await serveReleaseAsset(request, env, asset);
    }

    return notFound(request.method);
  } catch (error) {
    console.error("Failed to serve release download", error);
    return jsonResponse(
      {
        error: "internal_error",
        message: "The release download service could not complete the request.",
      },
      500,
      request.method,
    );
  }
}

export default {
  fetch: handleRequest,
} satisfies ExportedHandler<Env>;
