using POS.SharedKernel;

namespace POS.Identity.Domain;

/// <summary>
/// A tenant's override of one named, deployment-wide default.
/// </summary>
/// <remarks>
/// Generic and opaque by design: Identity stores a (tenant, key) → JSON mapping and
/// nothing more. It does not know that "purchasing.policy" deserializes into
/// <c>PurchasingPolicyOptions</c> — that would make Identity depend on Purchasing's
/// types, exactly the cross-module coupling ADR 002 forbids. Each consuming module's
/// own resolver owns interpreting its own key's value; Identity's job stops at "does
/// this tenant have an override, and what string did they save".
/// </remarks>
public sealed class TenantSetting : Entity<Guid>, ITenantScoped
{
    private TenantSetting() { }

    public static TenantSetting Create(
        Guid tenantId, string key, string value, Guid setByUserId, DateTimeOffset now) => new()
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            Key = key,
            Value = value,
            SetByUserId = setByUserId,
            SetAt = now
        };

    public Guid TenantId { get; private set; }

    /// <summary>e.g. "purchasing.policy". Stable — resolvers reference it by this exact string.</summary>
    public string Key { get; private set; } = null!;

    /// <summary>Opaque JSON. Never inspected or validated here — see this type's remarks.</summary>
    public string Value { get; private set; } = null!;

    public Guid SetByUserId { get; private set; }
    public DateTimeOffset SetAt { get; private set; }

    /// <summary>Setting is an upsert: a tenant has at most one override per key.</summary>
    public void Update(string value, Guid setByUserId, DateTimeOffset now)
    {
        Value = value;
        SetByUserId = setByUserId;
        SetAt = now;
    }
}
