using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class UserAgentTests
{
    [Fact]
    public void Default_agent_keeps_browser_form() =>
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Envoy/1.2.3", UserAgents.Default(ReleaseVersion.Parse("1.2.3-beta.1")));
}
