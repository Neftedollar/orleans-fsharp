# Legacy Resilience Example

> **Archived and unsupported.** This material is retained only to help migrate existing
> applications. There is no new Legacy release line, feature or compatibility work, or security
> fixes. New development must use the current functional API.

**Applying the current resilience helpers to an original-API grain handle.**

> The resilience library itself is current. Only this handle example belongs to the legacy authoring model.
> New actors use `grainContract`, `grainFor`, and `FunctionalGrain.ref`.

### Wrapping typed `FSharpGrain.ask` calls

```fsharp
let! price =
    GrainResilience.retry<decimal> 3 TimeSpan.Zero (fun () ->
        FSharpGrain.ask<PricingState, PricingCommand, decimal> (GetPrice itemId) pricingGrain)
```

---

For functional API records, pass the record-field call directly to `GrainResilience.execute`; see [Grain Resilience](../resilience.md).
