# Codebase Analysis Summary

Project: `dotnet-websocket-form` — a .NET 10 Binance public market-data library and Windows Forms monitor.

Analyzed commit: `5969adb7e14233a6fcd9aa5bab5b3a0bd4cfe62c`.

## Coverage and validation

All 16 files in the scanner inventory are represented: 10 categorized as code, 4 as configuration, and 2 as documentation. These are the scanner's categories; solution/build files and the JSONL sample are included in its code category. The inventory also includes `.ua/config.json`.

The graph contains 36 nodes: 10 file, 4 config, 2 document, 9 class, and 11 function nodes. It contains 94 edges: 31 contains, 16 exports, 15 depends_on, 7 calls, 3 inherits, 8 documents, 10 configures, and 4 tested_by.

All inventory paths, node and edge types, line ranges, endpoints, layer assignments, and tour references passed validation. Fingerprint generation succeeded for all 16 files. The graph is a selective architecture map, not an exhaustive member index or runtime trace.

## Architecture and tour

Four layers: Desktop Monitor; Market Streaming Library; Regression Checks And Samples; Project Support.

Seven tour steps:

1. Start The Desktop Monitor
2. Choose And Validate Subscriptions
3. Follow The Live Connection
4. Convert Messages Into Events
5. Render A Bounded View
6. Replay Without The Network
7. Build And Verify Changes

## Tool limitations and review findings

- Namespace import resolution returned no internal imports. Relationships are instead represented by source-grounded dependencies, calls, configurations, and documentation. The tour's imports/calls-only traversal therefore could not discover the entire file flow automatically; its remaining sequence uses verified summaries and layer assignments.
- Structural extraction skipped six unsupported solution/build/sample files. Their contents were read directly and all were represented; no inventory files were omitted.
- The merge tool misclassified the console regression harness filename and removed four valid test edges. Source review restored all four. BinanceClient coverage is explicitly limited to the optional live smoke test; it is not comprehensive transport testing. A future merge using the same classifier may need the same review.
- The only final validation warning is the isolated `.ua/config.json` node, which controls analysis language and has no application dependency edges.
- The previously missing graphology dependency was installed and batch analysis succeeded. The prebuilt viewer download failed, so the dashboard uses the local Vite server.

No C# source files were changed by this analysis. Temporary scripts and diagnostics are excluded from Git by `.ua/.gitignore`.
