using POS.Payments.Abstractions;
using POS.Payments.Domain;
using POS.Payments.Manual;
using POS.Payments.Orchestration;
using POS.SharedKernel;

namespace POS.UnitTests;

/// <summary>
/// Test doubles that record the ORDER of operations, not just their occurrence.
/// </summary>
/// <remarks>
/// The payment module's guarantees are almost entirely about sequencing, so a spy that
/// only records "was the store called" would pass against a completely unsafe
/// implementation. This one records a shared timeline.
/// </remarks>
internal sealed class RecordingPaymentStore : IPaymentStore
{
    public List<string> Timeline { get; } = [];
    public List<Payment> Committed { get; } = [];
    public Payment? ExistingByKey { get; set; }
    public Payment? ExistingById { get; set; }

    public Task AddAndCommitAsync(Payment payment, CancellationToken cancellationToken)
    {
        Timeline.Add("store.add-and-commit");
        Committed.Add(payment);
        return Task.CompletedTask;
    }

    public Task UpdateAndCommitAsync(Payment payment, CancellationToken cancellationToken)
    {
        Timeline.Add("store.update-and-commit");
        return Task.CompletedTask;
    }

    public Task<Payment?> FindByIdempotencyKeyAsync(
        Guid tenantId, IdempotencyKey key, CancellationToken cancellationToken)
    {
        Timeline.Add("store.find-by-key");
        return Task.FromResult(ExistingByKey);
    }

    public Task<Payment?> FindByIdAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken)
        => Task.FromResult(ExistingById);
}

internal sealed class ScriptedProvider(PaymentCapabilities capabilities) : IPaymentProvider
{
    public List<string> Timeline { get; set; } = [];
    public Func<PaymentRequest, PaymentOutcome>? OnAuthorise { get; set; }
    public Func<PaymentOutcome>? OnQuery { get; set; }
    public Func<RefundRequest, PaymentOutcome>? OnRefund { get; set; }
    public Exception? ThrowOnAuthorise { get; set; }

    public string ProviderCode => "SCRIPTED";
    public PaymentCapabilities Capabilities => capabilities;

    public Task<PaymentOutcome> AuthoriseAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        Timeline.Add("provider.authorise");

        if (ThrowOnAuthorise is not null)
        {
            throw ThrowOnAuthorise;
        }

        return Task.FromResult(OnAuthorise?.Invoke(request)
            ?? new PaymentOutcome { Status = PaymentOutcomeStatus.Captured, ProviderReference = "ref-1" });
    }

    public Task<PaymentOutcome> CaptureAsync(string r, Money a, CancellationToken c)
        => Task.FromResult(new PaymentOutcome { Status = PaymentOutcomeStatus.Captured });

    public Task<PaymentOutcome> VoidAsync(string r, CancellationToken c)
        => Task.FromResult(new PaymentOutcome { Status = PaymentOutcomeStatus.Voided });

    public Task<PaymentOutcome> RefundAsync(RefundRequest request, CancellationToken c)
    {
        Timeline.Add("provider.refund");
        return Task.FromResult(OnRefund?.Invoke(request)
            ?? new PaymentOutcome { Status = PaymentOutcomeStatus.Refunded, ProviderReference = "refund-1" });
    }

    public Task<PaymentOutcome> QueryAsync(IdempotencyKey key, CancellationToken c)
    {
        Timeline.Add("provider.query");
        return Task.FromResult(OnQuery?.Invoke()
            ?? new PaymentOutcome { Status = PaymentOutcomeStatus.Unknown });
    }
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}

public sealed class PaymentAggregateTests
{
    private static readonly Money Forty = new(40m, "GBP");
    private static readonly DateTimeOffset At = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    private static Payment NewPayment(Money? amount = null) => Payment.Initiate(
        Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        PaymentKind.Sale, amount ?? Forty, IdempotencyKey.New(), "SCRIPTED", At,
        BusinessDate.Open(new DateOnly(2026, 7, 22)));

    [Fact]
    public void A_new_payment_starts_initiated_with_nothing_captured()
    {
        var payment = NewPayment();

        payment.Status.ShouldBe(PaymentStatus.Initiated);
        payment.CapturedAmount.Amount.ShouldBe(0m);
        payment.RefundedAmount.Amount.ShouldBe(0m);
        payment.IsFinal.ShouldBeFalse();
    }

    [Fact]
    public void A_payment_cannot_be_initiated_for_a_non_positive_amount()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => NewPayment(new Money(0m, "GBP")));
        Should.Throw<ArgumentOutOfRangeException>(() => NewPayment(new Money(-5m, "GBP")));
    }

    [Fact]
    public void Capture_may_follow_initiation_directly_for_providers_without_a_separate_auth()
    {
        var payment = NewPayment();

        payment.MarkCaptured(Forty, "ref", At).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Captured);
    }

    [Fact]
    public void Capture_cannot_exceed_the_requested_amount()
    {
        var payment = NewPayment();

        var result = payment.MarkCaptured(new Money(41m, "GBP"), "ref", At);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("payment.capture_exceeds_authorisation");
    }

    [Fact]
    public void Capture_in_a_different_currency_is_refused()
    {
        var payment = NewPayment();

        payment.MarkCaptured(new Money(40m, "EUR"), "ref", At)
               .Error.Code.ShouldBe("payment.currency_mismatch");
    }

    [Fact]
    public void A_settled_payment_cannot_be_captured_again()
    {
        var payment = NewPayment();
        payment.MarkCaptured(Forty, "ref", At);
        payment.MarkSettled(At);

        payment.MarkCaptured(Forty, "ref", At).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Indeterminate_is_reachable_from_initiated_and_is_not_final()
    {
        var payment = NewPayment();

        payment.MarkIndeterminate("timeout", At).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Indeterminate);
        payment.IsFinal.ShouldBeFalse("an unresolved payment is not a finished one");
        payment.Attempts.Count.ShouldBe(1);
    }

    [Fact]
    public void An_indeterminate_payment_can_still_be_resolved_to_captured()
    {
        var payment = NewPayment();
        payment.MarkIndeterminate("timeout", At);

        payment.MarkCaptured(Forty, "ref", At).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Captured);
    }

    [Fact]
    public void A_final_payment_cannot_become_indeterminate()
    {
        var payment = NewPayment();
        payment.MarkDeclined("51", "insufficient funds", At);

        payment.MarkIndeterminate("timeout", At).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Refunds_accumulate_and_cannot_exceed_what_was_captured()
    {
        var payment = NewPayment();
        payment.MarkCaptured(Forty, "ref", At);

        payment.RegisterRefund(new Money(15m, "GBP")).IsSuccess.ShouldBeTrue();
        payment.RegisterRefund(new Money(20m, "GBP")).IsSuccess.ShouldBeTrue();
        payment.RefundedAmount.Amount.ShouldBe(35m);
        payment.RefundableAmount.Amount.ShouldBe(5m);

        var overdraw = payment.RegisterRefund(new Money(10m, "GBP"));
        overdraw.IsFailure.ShouldBeTrue();
        overdraw.Error.Code.ShouldBe("payment.refund_exceeds_refundable");
        payment.RefundedAmount.Amount.ShouldBe(35m, "a rejected refund must not mutate the total");
    }

    [Fact]
    public void An_uncaptured_payment_cannot_be_refunded()
    {
        var payment = NewPayment();

        payment.RegisterRefund(new Money(5m, "GBP"))
               .Error.Code.ShouldBe("payment.not_refundable");
    }

    [Fact]
    public void A_partly_refunded_payment_cannot_be_voided()
    {
        var payment = NewPayment();
        payment.MarkCaptured(Forty, "ref", At);
        payment.RegisterRefund(new Money(10m, "GBP"));

        payment.Void(At, "changed mind").Error.Code.ShouldBe("payment.void_after_refund");
    }

    [Fact]
    public void Only_a_refund_may_be_linked_to_an_original()
    {
        var sale = NewPayment();

        sale.LinkToOriginal(Guid.NewGuid()).Error.Code.ShouldBe("payment.not_a_refund");
    }

    [Fact]
    public void An_idempotency_key_must_not_be_blank()
    {
        Should.Throw<ArgumentException>(() => new IdempotencyKey("  "));
        Should.Throw<ArgumentException>(() => new IdempotencyKey(new string('x', 101)));
    }
}

public sealed class PaymentOrchestratorTests
{
    private static readonly Money Forty = new(40m, "GBP");
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    private static PaymentCapabilities Caps(bool offline = true, Money? floor = null) => new()
    {
        SeparatesAuthAndCapture = false,
        SupportsOfflineAuthorisation = offline,
        OfflineFloorLimit = floor,
        SupportsPartialRefund = true,
        SupportsVoid = true,
        SupportsTokenisation = false,
        AuthorisationTimeout = TimeSpan.FromSeconds(30),
    };

    private static PaymentIntent Intent(bool offline = false, Money? amount = null) => new()
    {
        TenantId = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        TerminalId = Guid.NewGuid(),
        SaleId = Guid.NewGuid(),
        Amount = amount ?? Forty,
        IdempotencyKey = IdempotencyKey.New(),
        ProviderCode = "SCRIPTED",
        Reference = "SALE-1",
        BusinessDate = BusinessDate.Open(new DateOnly(2026, 7, 22)),
        TerminalIsOffline = offline,
    };

    private static (PaymentOrchestrator Orchestrator, RecordingPaymentStore Store, ScriptedProvider Provider)
        Build(PaymentCapabilities? capabilities = null)
    {
        var store = new RecordingPaymentStore();
        var provider = new ScriptedProvider(capabilities ?? Caps()) { Timeline = store.Timeline };
        var registry = new PaymentProviderRegistry([provider]);
        return (new PaymentOrchestrator(registry, store, new FixedClock(Now)), store, provider);
    }

    /// <summary>
    /// The single most important test in the module.
    /// </summary>
    /// <remarks>
    /// If the provider is called before the record is durable, a crash in between
    /// leaves money moved with no local evidence — undetectable by any query we can
    /// run. Asserting the timeline rather than the end state is the only way to catch a
    /// refactor that reorders these.
    /// </remarks>
    [Fact]
    public async Task The_payment_record_is_committed_before_the_provider_is_called()
    {
        var (orchestrator, store, _) = Build();

        await orchestrator.PayAsync(Intent());

        var commit = store.Timeline.IndexOf("store.add-and-commit");
        var call = store.Timeline.IndexOf("provider.authorise");

        (commit >= 0).ShouldBeTrue("the record must have been committed at all");
        (call > commit).ShouldBeTrue(
            "the payment must be durable before any request leaves the building (ADR 042)");
    }

    [Fact]
    public async Task A_successful_authorisation_is_recorded_as_captured()
    {
        var (orchestrator, _, _) = Build();

        var result = await orchestrator.PayAsync(Intent());

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(PaymentStatus.Captured);
    }

    [Fact]
    public async Task A_timeout_is_recorded_as_indeterminate_and_never_as_failed()
    {
        var (orchestrator, _, provider) = Build();
        provider.ThrowOnAuthorise = new TimeoutException("no response");

        var result = await orchestrator.PayAsync(Intent());

        result.Value.Status.ShouldBe(PaymentStatus.Indeterminate,
            "a lost response means an unknown outcome; recording it as Failed is what " +
            "double-charges the customer on retry");
    }

    [Fact]
    public async Task Cancellation_is_also_indeterminate_because_the_request_may_have_landed()
    {
        var (orchestrator, _, provider) = Build();
        provider.ThrowOnAuthorise = new OperationCanceledException();

        var result = await orchestrator.PayAsync(Intent());

        result.Value.Status.ShouldBe(PaymentStatus.Indeterminate);
    }

    [Fact]
    public async Task Replaying_the_same_idempotency_key_returns_the_original_and_calls_nobody()
    {
        var (orchestrator, store, provider) = Build();
        var first = await orchestrator.PayAsync(Intent());

        store.ExistingByKey = first.Value;
        store.Timeline.Clear();

        var replay = await orchestrator.PayAsync(Intent());

        replay.Value.Id.ShouldBe(first.Value.Id);
        store.Timeline.ShouldNotContain("provider.authorise",
            "a retry must never produce a second authorisation");
    }

    [Fact]
    public async Task Retrying_an_unresolved_payment_is_refused_outright()
    {
        var (orchestrator, store, _) = Build();
        var stuck = (await orchestrator.PayAsync(Intent())).Value;
        stuck.MarkIndeterminate("timeout", Now);
        store.ExistingByKey = stuck;

        var retry = await orchestrator.PayAsync(Intent());

        retry.IsFailure.ShouldBeTrue();
        retry.Error.Code.ShouldBe("payment.prior_attempt_unresolved");
    }

    [Fact]
    public async Task An_offline_payment_is_refused_when_the_provider_will_not_stand_behind_it()
    {
        var (orchestrator, store, _) = Build(Caps(offline: false));

        var result = await orchestrator.PayAsync(Intent(offline: true));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("payment.offline_unsupported");
        store.Committed.ShouldBeEmpty("a payment we refuse to attempt leaves no record");
    }

    [Fact]
    public async Task An_offline_payment_over_the_floor_limit_is_refused()
    {
        var (orchestrator, _, _) = Build(Caps(floor: new Money(25m, "GBP")));

        var result = await orchestrator.PayAsync(Intent(offline: true));

        result.Error.Code.ShouldBe("payment.over_floor_limit");
    }

    [Fact]
    public async Task An_offline_payment_under_the_floor_limit_proceeds()
    {
        var (orchestrator, _, _) = Build(Caps(floor: new Money(100m, "GBP")));

        var result = await orchestrator.PayAsync(Intent(offline: true));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_provider_code_is_an_error_and_not_a_fallback()
    {
        var (orchestrator, store, _) = Build();

        var intent = Intent() with { ProviderCode = "NOT_REGISTERED" };
        var result = await orchestrator.PayAsync(intent);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("payment.unknown_provider");
        store.Committed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolving_an_indeterminate_payment_asks_the_provider()
    {
        var (orchestrator, store, provider) = Build();
        var stuck = (await orchestrator.PayAsync(Intent())).Value;
        stuck.MarkIndeterminate("timeout", Now);
        store.ExistingById = stuck;
        provider.OnQuery = () => new PaymentOutcome
        {
            Status = PaymentOutcomeStatus.Captured,
            ProviderReference = "found",
            ApprovedAmount = Forty,
        };

        var resolved = await orchestrator.ResolveAsync(stuck.TenantId, stuck.Id);

        resolved.Value.Status.ShouldBe(PaymentStatus.Captured);
    }

    [Fact]
    public async Task A_provider_with_no_record_means_the_payment_definitively_failed()
    {
        var (orchestrator, store, provider) = Build();
        var stuck = (await orchestrator.PayAsync(Intent())).Value;
        stuck.MarkIndeterminate("timeout", Now);
        store.ExistingById = stuck;
        provider.OnQuery = () => new PaymentOutcome { Status = PaymentOutcomeStatus.NotFound };

        var resolved = await orchestrator.ResolveAsync(stuck.TenantId, stuck.Id);

        resolved.Value.Status.ShouldBe(PaymentStatus.Failed,
            "NotFound is a clean negative: the request never arrived, so no money moved");
    }

    [Fact]
    public async Task A_failure_while_resolving_leaves_the_payment_indeterminate()
    {
        var (orchestrator, store, provider) = Build();
        var stuck = (await orchestrator.PayAsync(Intent())).Value;
        stuck.MarkIndeterminate("timeout", Now);
        store.ExistingById = stuck;
        provider.OnQuery = () => throw new HttpRequestExceptionStub();

        var resolved = await orchestrator.ResolveAsync(stuck.TenantId, stuck.Id);

        resolved.Value.Status.ShouldBe(PaymentStatus.Indeterminate,
            "a failed resolution attempt must not invent an outcome");
    }

    [Fact]
    public async Task A_refund_is_a_new_linked_payment_and_never_a_mutation()
    {
        var (orchestrator, store, _) = Build();
        var original = (await orchestrator.PayAsync(Intent())).Value;
        store.ExistingById = original;
        store.ExistingByKey = null;

        var refund = await orchestrator.RefundAsync(new RefundIntent
        {
            TenantId = original.TenantId,
            TerminalId = original.TerminalId,
            OriginalPaymentId = original.Id,
            Amount = new Money(10m, "GBP"),
            IdempotencyKey = IdempotencyKey.New(),
            Reason = "damaged",
            BusinessDate = BusinessDate.Open(new DateOnly(2026, 7, 22)),
        });

        refund.IsSuccess.ShouldBeTrue();
        refund.Value.Id.ShouldNotBe(original.Id);
        refund.Value.Kind.ShouldBe(PaymentKind.Refund);
        refund.Value.OriginalPaymentId.ShouldBe(original.Id);
        original.RefundedAmount.Amount.ShouldBe(10m);
    }

    [Fact]
    public async Task An_over_refund_is_refused_before_anything_is_written()
    {
        var (orchestrator, store, _) = Build();
        var original = (await orchestrator.PayAsync(Intent())).Value;
        store.ExistingById = original;
        store.ExistingByKey = null;
        store.Committed.Clear();

        var refund = await orchestrator.RefundAsync(new RefundIntent
        {
            TenantId = original.TenantId,
            TerminalId = original.TerminalId,
            OriginalPaymentId = original.Id,
            Amount = new Money(999m, "GBP"),
            IdempotencyKey = IdempotencyKey.New(),
            Reason = "oops",
            BusinessDate = BusinessDate.Open(new DateOnly(2026, 7, 22)),
        });

        refund.IsFailure.ShouldBeTrue();
        store.Committed.ShouldBeEmpty("an invalid refund must leave no orphan record");
    }
}

internal sealed class HttpRequestExceptionStub : Exception;

public sealed class ManualCardProviderTests
{
    private sealed class Prompt(ManualApproval approval) : IManualApprovalPrompt
    {
        public Task<ManualApproval> PromptAsync(Money a, CancellationToken c) => Task.FromResult(approval);

        public Task<ManualApproval> PromptRefundAsync(Money a, CancellationToken c) => Task.FromResult(approval);
    }

    private static PaymentRequest Request(bool offline = false) => new()
    {
        PaymentId = Guid.NewGuid(),
        IdempotencyKey = IdempotencyKey.New(),
        Amount = new Money(40m, "GBP"),
        TerminalId = Guid.NewGuid(),
        Reference = "SALE-1",
        TerminalIsOffline = offline,
    };

    [Fact]
    public async Task An_approved_card_is_captured_immediately_not_merely_authorised()
    {
        var provider = new ManualCardProvider(new Prompt(new ManualApproval(true, "A1234", "1234", "VISA")));

        var outcome = await provider.AuthoriseAsync(Request(), default);

        outcome.Status.ShouldBe(PaymentOutcomeStatus.Captured,
            "the money has already moved on the bank's own device");
        outcome.Instrument!.MaskedPan.ShouldBe("1234");
    }

    [Fact]
    public async Task A_rejected_card_is_declined()
    {
        var provider = new ManualCardProvider(new Prompt(new ManualApproval(false, null, Note: "no funds")));

        var outcome = await provider.AuthoriseAsync(Request(), default);

        outcome.Status.ShouldBe(PaymentOutcomeStatus.Declined);
    }

    [Fact]
    public async Task Offline_is_supported_because_our_connectivity_is_irrelevant_here()
    {
        var provider = new ManualCardProvider(new Prompt(new ManualApproval(true, "A1")));

        provider.Capabilities.SupportsOfflineAuthorisation.ShouldBeTrue();
        (await provider.AuthoriseAsync(Request(offline: true), default))
            .Status.ShouldBe(PaymentOutcomeStatus.Captured);
    }

    [Fact]
    public async Task Void_is_refused_rather_than_falsely_reported_as_succeeding()
    {
        var provider = new ManualCardProvider(new Prompt(new ManualApproval(true, "A1")));

        (await provider.VoidAsync("ref", default)).Status.ShouldBe(PaymentOutcomeStatus.Failed);
        provider.Capabilities.SupportsVoid.ShouldBeFalse();
    }

    [Fact]
    public async Task Query_returns_unknown_rather_than_pretending_there_is_no_record()
    {
        var provider = new ManualCardProvider(new Prompt(new ManualApproval(true, "A1")));

        (await provider.QueryAsync(IdempotencyKey.New(), default))
            .Status.ShouldBe(PaymentOutcomeStatus.Unknown,
                "claiming NotFound would let the orchestrator mark it failed and invite a retry");
    }

    [Fact]
    public void An_unregistered_provider_resolves_to_an_error()
    {
        var registry = new PaymentProviderRegistry(
            [new ManualCardProvider(new Prompt(new ManualApproval(true, "A1")))]);

        registry.Resolve("SOMETHING_ELSE").IsFailure.ShouldBeTrue();
        registry.Resolve(ManualCardProvider.Code).IsSuccess.ShouldBeTrue();
    }
}
