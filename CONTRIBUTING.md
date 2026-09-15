# Contributing to Qvec

Qvec uses xUnit for automated tests. Add or update tests with every behavior change, and make each test assert the correct behavior the library should provide.

## Test categories

- Use `[Trait("Category", TestCategories.KnownDefect)]` only for assertions that intentionally document a defect that has not been fixed yet. These tests must still assert the desired correct behavior; they are excluded from the fast CI gate until the defect is remediated.
- Use `[Trait("Category", TestCategories.Slow)]` for long-running quality or stress tests. CI runs these in a separate slow-test step after the fast gate.
- Do not weaken assertions to match broken behavior. If behavior is wrong, write the test for the intended behavior and mark it `KnownDefect` until the implementation is fixed.

Run the usual local checks before sending changes:

```bash
dotnet build --nologo
dotnet test --nologo --filter "Category!=Slow"
```
