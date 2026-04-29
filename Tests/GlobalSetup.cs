using SfwPlayer.Platform;

namespace Tests;

[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public void Setup()
    {
        VlcSetup.Initialize();
        VlcSetup.ActivateApp();
    }
}
