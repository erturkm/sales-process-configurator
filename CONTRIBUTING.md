# Contributing

Thanks for your interest. Please note up front that this is a **demonstration accelerator**
maintained on a best-effort, personal basis. There is **no support commitment and no service level
agreement** — issues and pull requests may be reviewed slowly, or not at all. See the
[DISCLAIMER](DISCLAIMER.md).

## Reporting issues

When opening an issue, please include:

- what you expected to happen and what actually happened;
- the step script or feature involved;
- the Dataverse region and whether the solution was imported or built from source;
- the full error text, including any Dataverse error code (for example `0x80071151`).

Please **do not** include tenant identifiers, org URLs, access tokens, personal data or customer
data in issues.

## Pull requests

- Keep changes focused; unrelated refactors are hard to review.
- Build scripts must stay **idempotent** — re-running any step must be safe.
- Web resource changes should be tested in a browser before submitting.
- Do not commit strong-name keys, tokens, org URLs or any tenant-specific values.
- By contributing you agree your contributions are licensed under the [MIT Licence](LICENSE).

## Security

Please do not report suspected security issues in public issues. Since this project is explicitly
**not intended for production use**, the appropriate response to a security concern is to stop using
it in any sensitive environment.
