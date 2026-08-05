namespace POS.SharedKernel;

/// <summary>
/// A monetary amount in a specific currency.
/// </summary>
/// <remarks>
/// <para><c>decimal</c> only. Binary floating point cannot represent 0.10 exactly,
/// which produces receipts that do not reconcile and totals that drift over a
/// day's trading.</para>
/// <para>Arithmetic across currencies throws rather than silently coercing.
/// A currency mismatch is always a bug, never a runtime condition to tolerate.</para>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(currency));

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public decimal Amount { get; }
    public string Currency { get; }

    /// <summary>
    /// True for <c>default(Money)</c>, which bypasses the constructor and therefore
    /// has a null currency.
    /// </summary>
    /// <remarks>
    /// The unavoidable trap of a struct value object: <c>default(Money)</c> cannot be
    /// prevented by any constructor, and it arises legitimately — an uninitialised
    /// field, an EF materialisation of a null column, <c>Array.Empty&lt;Money&gt;()</c>
    /// grown by resizing, or <c>list.FirstOrDefault()</c> on an empty sequence.
    ///
    /// Rather than let it propagate silently — where it behaves as "zero of no
    /// currency" and quietly poisons a total — every operation checks and throws a
    /// message that names the real problem. A confusing "currency mismatch between
    /// and USD" wastes an afternoon; "uninitialised Money" does not.
    /// </remarks>
    public bool IsUninitialised => Currency is null;

    public static Money Zero(string currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;
    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    /// <summary>Multiplies by a unitless factor — a quantity or a discount rate.</summary>
    public static Money operator *(Money money, decimal factor) => new(money.Amount * factor, money.Currency);

    public static bool operator >(Money a, Money b) { EnsureSameCurrency(a, b); return a.Amount > b.Amount; }
    public static bool operator <(Money a, Money b) { EnsureSameCurrency(a, b); return a.Amount < b.Amount; }
    public static bool operator >=(Money a, Money b) { EnsureSameCurrency(a, b); return a.Amount >= b.Amount; }
    public static bool operator <=(Money a, Money b) { EnsureSameCurrency(a, b); return a.Amount <= b.Amount; }

    /// <summary>
    /// Rounds to the given number of decimal places using commercial rounding
    /// (0.5 rounds away from zero).
    /// </summary>
    /// <remarks>
    /// This is a POLICY DECISION, not a technical default, and it is deliberately
    /// the only rounding entry point in the system. Banker's rounding
    /// (<see cref="MidpointRounding.ToEven"/>) is the .NET default and is correct in
    /// some jurisdictions; commercial rounding is expected in most retail contexts.
    ///
    /// Whichever you choose, it must be applied consistently, and you must separately
    /// decide whether tax rounds per line or per invoice — the two produce different
    /// totals and tax authorities care which you used.
    ///
    /// Never call Math.Round on a monetary value anywhere else in the codebase.
    /// </remarks>
    public Money Round(int decimals = 2) =>
        new(Math.Round(Amount, decimals, MidpointRounding.AwayFromZero), Currency);

    /// <summary>Divides by a unitless divisor — deriving a unit cost from a total, for example.</summary>
    /// <remarks>
    /// Deliberately does NOT round. A weighted average unit cost must retain full
    /// decimal precision in storage and be rounded only for display, because
    /// rounding a unit cost to 2dp and then multiplying back up by quantity
    /// reintroduces exactly the drift this type exists to prevent. See ADR 020.
    /// </remarks>
    public static Money operator /(Money money, decimal divisor)
    {
        if (divisor == 0m)
            throw new DivideByZeroException("Cannot divide a monetary amount by zero.");

        EnsureInitialised(money);
        return new Money(money.Amount / divisor, money.Currency);
    }

    /// <summary>
    /// The number of decimal places in which this currency is actually settled.
    /// </summary>
    /// <remarks>
    /// Two is not universal. Rounding a yen amount to 2dp invents fractional yen that
    /// no payment terminal can take, and rounding a dinar to 2dp loses a real
    /// settleable digit. The exceptions are few and stable, so a lookup is honest and
    /// cheap; the alternative — a currency reference table — is deferred to the point
    /// where a customer needs a currency not listed here.
    /// </remarks>
    public int DecimalPlaces => Currency switch
    {
        "JPY" or "KRW" or "CLP" or "ISK" or "VND" or "PYG" or "RWF" or "UGX" or "XAF" or "XOF" => 0,
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
        _ => 2
    };

    /// <summary>Rounds to the natural precision of the currency.</summary>
    public Money RoundToCurrency() => Round(DecimalPlaces);

    /// <summary>
    /// Splits this amount into <paramref name="parts"/> shares that sum EXACTLY back
    /// to the original, distributing any indivisible remainder one minor unit at a time.
    /// </summary>
    /// <remarks>
    /// The penny-allocation problem, which naive division gets wrong every time:
    /// 100.00 across three lines is 33.333…, which rounds to 33.33 and sums to 99.99.
    /// One cent has vanished. In a POS that cent appears as an unbalanced sale, a
    /// stock valuation that will not reconcile, or a tax total the authority disputes.
    ///
    /// Largest-remainder distribution instead: floor every share, then hand the
    /// leftover minor units to the earliest shares. The result always sums to the
    /// input, which is the only property that matters.
    ///
    /// Used in Phase 4 to apportion landed costs (freight, duty) across receipt lines,
    /// and in Phase 5 to spread an invoice-level discount across sale lines.
    /// </remarks>
    public Money[] Allocate(int parts)
    {
        if (parts <= 0)
            throw new ArgumentOutOfRangeException(nameof(parts), "Must allocate into at least one part.");

        return Allocate(Enumerable.Repeat(1m, parts).ToArray());
    }

    /// <summary>Splits this amount in proportion to the given weights, summing exactly to the original.</summary>
    public Money[] Allocate(IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        EnsureInitialised(this);

        if (weights.Count == 0)
            throw new ArgumentException("At least one weight is required.", nameof(weights));

        if (weights.Any(w => w < 0m))
            throw new ArgumentException("Weights cannot be negative.", nameof(weights));

        var totalWeight = weights.Sum();
        if (totalWeight == 0m)
            throw new ArgumentException("Weights must not sum to zero.", nameof(weights));

        // Work in whole minor units so the remainder is an exact integer count.
        var scale = Pow10(DecimalPlaces);
        var totalMinorUnits = (long)Math.Round(Amount * scale, 0, MidpointRounding.AwayFromZero);

        var shares = new long[weights.Count];
        long distributed = 0;

        for (var i = 0; i < weights.Count; i++)
        {
            // Truncate toward zero so the remainder always has the same sign as the
            // total — otherwise a refund (negative) would allocate in the wrong direction.
            shares[i] = (long)(totalMinorUnits * weights[i] / totalWeight);
            distributed += shares[i];
        }

        var remainder = totalMinorUnits - distributed;
        var step = remainder < 0 ? -1 : 1;

        for (var i = 0; remainder != 0; i = (i + 1) % weights.Count)
        {
            // Skip zero-weight shares: they asked for nothing and must receive nothing.
            if (weights[i] == 0m) continue;

            shares[i] += step;
            remainder -= step;
        }

        // `Currency` cannot be read inside the lambda: Money is a struct, and a
        // lambda capturing `this` from a struct is illegal (CS1673). Copy first.
        var currency = Currency;
        return shares.Select(s => new Money(s / scale, currency)).ToArray();
    }

    private static decimal Pow10(int exponent) => exponent switch
    {
        0 => 1m,
        1 => 10m,
        2 => 100m,
        3 => 1000m,
        _ => throw new ArgumentOutOfRangeException(nameof(exponent))
    };

    private static void EnsureInitialised(Money value)
    {
        if (value.IsUninitialised)
            throw new InvalidOperationException("Operation on an uninitialised Money value.");
    }

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <remarks>
    /// Formats to the currency's own precision, not a hardcoded two places —
    /// otherwise ¥1000 renders as "1000.00 JPY", which invents a precision the
    /// currency does not have. Diagnostic and log formatting only; user-facing
    /// output must go through culture-aware formatting in the presentation layer.
    /// </remarks>
    public override string ToString() =>
        IsUninitialised
            ? "<uninitialised Money>"
            : Amount.ToString("F" + DecimalPlaces, System.Globalization.CultureInfo.InvariantCulture)
              + " " + Currency;

    private static void EnsureSameCurrency(Money a, Money b)
    {
        if (a.IsUninitialised || b.IsUninitialised)
        {
            throw new InvalidOperationException(
                "Operation on an uninitialised Money value. This is default(Money), " +
                "which has no currency — check for an unassigned field, an empty " +
                "FirstOrDefault(), or a nullable database column materialised as default.");
        }

        if (a.Currency != b.Currency)
            throw new InvalidOperationException($"Currency mismatch: {a.Currency} and {b.Currency}.");
    }
}
