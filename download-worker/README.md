# ZZZ download Worker

Cloudflare Worker for serving ZZZ browser release executables from the private
`zzz-releases` R2 bucket through `download.zzz.campusphere.ltd`.

## Routes

- `GET|HEAD /` returns service health JSON.
- `GET|HEAD /latest/x64`, `/latest/x86`, and `/latest/arm64` redirect to the
  corresponding versioned v2.1.5 object.
- `GET|HEAD /releases/{version}/{filename.exe}` streams an R2 object as an
  attachment. Versioned responses use immutable one-year caching and support a
  single HTTP byte range.
- `OPTIONS` returns CORS preflight headers. Other methods return `405`.

R2 objects must use the same path as their public versioned URL, without the
leading slash. For example:

```text
releases/v2.1.5/ZZZ-v2.1.5-win-x64.exe
```

## Local verification

```powershell
npm install
npm run check
npm test
npm run build
```

`npm run build` performs a Wrangler dry run only. Deployment is intentionally
separate and must be invoked explicitly when the R2 bucket is populated.
