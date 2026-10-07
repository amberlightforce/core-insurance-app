# WireMock doubles

Every external integration (AADE myDATA, gov.gr, bureau, banks, SMS, ...) has a WireMock double here and
switches to the real sandbox by configuration (`Integrations__<Name>__BaseUrl`, INFRASTRUCTURE §4 rule 8).

- `mappings/` holds request/response stubs (one sub-folder per integration, e.g. `mappings/mydata/`).
- `__files/` holds response bodies referenced by `bodyFileName`.
- Locally WireMock listens on <http://localhost:8089>; inside Compose the services reach it at `http://wiremock:8080`.
- Admin API: <http://localhost:8089/__admin/mappings>.

No integration exists yet, so both folders are empty. Each integration package adds its stubs together with its
contract tests.
