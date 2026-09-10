using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Tenancy;

namespace TripsAgent.UnitTests.Tenancy;

public class TenantContextTests
{
    [Fact]
    public void A_fresh_context_has_no_tenant()
    {
        var context = new TenantContext();

        context.HasTenant.Should().BeFalse();
        context.AgencyId.Should().BeNull();

        // Everything downstream reads this to decide what a request may see; unresolved must
        // mean "nothing", never "everything".
    }

    [Fact]
    public void Setting_a_tenant_defaults_the_root_to_the_agency_itself()
    {
        var agencyId = Guid.CreateVersion7();
        var context = new TenantContext();

        context.SetTenant(agencyId);

        context.AgencyId.Should().Be(agencyId);
        context.RootAgencyId.Should().Be(agencyId, "a principal is its own root");
        context.HasTenant.Should().BeTrue();
    }

    [Fact]
    public void A_sub_agent_keeps_its_principal_as_the_root()
    {
        var principalId = Guid.CreateVersion7();
        var subAgentId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var context = new TenantContext();
        context.SetTenant(subAgentId, principalId, userId);

        context.AgencyId.Should().Be(subAgentId);
        context.RootAgencyId.Should().Be(principalId);
        context.UserId.Should().Be(userId);
    }

    [Fact]
    public void An_empty_agency_id_is_refused()
    {
        var context = new TenantContext();

        var act = () => context.SetTenant(Guid.Empty);

        // Guid.Empty matches no rows, so accepting it would turn a configuration mistake into a
        // request that silently returns nothing and looks like a data problem.
        act.Should().Throw<ArgumentException>().WithMessage("*match no rows*");
    }

    [Fact]
    public void The_tenant_cannot_be_changed_once_set()
    {
        var context = new TenantContext();
        context.SetTenant(Guid.CreateVersion7());

        var act = () => context.SetTenant(Guid.CreateVersion7());

        // Re-assigning mid-request would let entities loaded under one agency be saved under
        // another — the exact leak the whole mechanism exists to stop.
        act.Should().Throw<InvalidOperationException>().WithMessage("*already set*");
    }

    [Fact]
    public void A_platform_user_has_an_identity_but_no_agency()
    {
        var userId = Guid.CreateVersion7();
        var context = new TenantContext();

        context.SetPlatformUser(userId);

        context.UserId.Should().Be(userId);
        context.HasTenant.Should().BeFalse("platform staff do not belong to an agency");
    }
}

public class PlatformScopeTests
{
    private static PlatformScope NewScope(out TenantContext tenant)
    {
        tenant = new TenantContext();
        return new PlatformScope(tenant, NullLogger<PlatformScope>.Instance);
    }

    [Fact]
    public void A_scope_is_inactive_until_it_is_entered()
    {
        var scope = NewScope(out _);

        scope.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Entering_activates_and_disposing_deactivates()
    {
        var scope = NewScope(out _);

        using (scope.Enter("platform reporting"))
        {
            scope.IsActive.Should().BeTrue();
        }

        scope.IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_scope_without_a_reason_is_refused(string? reason)
    {
        var scope = NewScope(out _);

        var act = () => scope.Enter(reason!);

        // The reason is the audit record. Without it the log entry says only that somebody read
        // across agencies, which is exactly the question a reviewer needs answered.
        act.Should().Throw<ArgumentException>().WithMessage("*must say why*");
    }

    [Fact]
    public void Nested_scopes_are_counted_not_flattened()
    {
        var scope = NewScope(out _);

        using (scope.Enter("outer"))
        {
            using (scope.Enter("inner"))
            {
                scope.IsActive.Should().BeTrue();
            }

            scope.IsActive.Should().BeTrue("the outer scope is still open");
        }

        scope.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Disposing_the_same_handle_twice_does_not_close_an_outer_scope()
    {
        var scope = NewScope(out _);

        using var outer = scope.Enter("outer");
        var inner = scope.Enter("inner");

        inner.Dispose();
        inner.Dispose();

        // A double dispose — a using block plus an explicit call — must not decrement twice and
        // quietly reinstate the filter while the outer scope believes it is still open.
        scope.IsActive.Should().BeTrue();
    }
}
