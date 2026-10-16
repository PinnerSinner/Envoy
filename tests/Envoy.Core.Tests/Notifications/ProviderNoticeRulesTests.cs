using Envoy.Core.Notifications;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Notifications;

public class ProviderNoticeRulesTests
{
    [Theory]
    [InlineData(ProviderState.Unknown, ProviderNoticeRule.None)]
    [InlineData(ProviderState.Healthy, ProviderNoticeRule.Resolve)]
    [InlineData(ProviderState.Degraded, ProviderNoticeRule.None)]
    [InlineData(ProviderState.Throttled, ProviderNoticeRule.None)]
    [InlineData(ProviderState.CoolingDown, ProviderNoticeRule.Submit)]
    [InlineData(ProviderState.AuthFailed, ProviderNoticeRule.Submit)]
    [InlineData(ProviderState.QuotaExceeded, ProviderNoticeRule.Submit)]
    public void Rule_per_state(ProviderState state, ProviderNoticeRule expected) => Assert.Equal(expected, ProviderNoticeRules.For(state));

    [Fact]
    public void Every_state_has_a_row_and_submit_matches_the_factory()
    {
        foreach (var state in Enum.GetValues<ProviderState>())
        {
            var submit = ProviderNoticeRules.For(state) == ProviderNoticeRule.Submit;
            Assert.Equal(submit, NoticeFactory.ProviderFailing("deepl", "DeepL", state) is not null);
        }
    }
}
