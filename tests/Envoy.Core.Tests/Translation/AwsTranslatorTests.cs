using Envoy.Core.Translation.Providers;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class AwsTranslatorTests
{
    [Fact]
    public void Config_disables_sdk_retries()
    {
        using var aws = new AwsTranslator("AKIAABCDEFGHIJKLMNOP", "aws-secret-0123456789", "eu-west-1", TimeSpan.FromSeconds(6));
        Assert.Equal(0, aws.Config.MaxErrorRetry);
        Assert.Equal(TimeSpan.FromSeconds(6), aws.Config.Timeout);
        Assert.Equal("eu-west-1", aws.Config.RegionEndpoint.SystemName);

        using var defaults = new AwsTranslator("AKIAABCDEFGHIJKLMNOP", "aws-secret-0123456789", "");
        Assert.Equal(AwsTranslator.DefaultTimeout, defaults.Config.Timeout);
        Assert.Equal("us-east-1", defaults.Config.RegionEndpoint.SystemName);
    }
}
