/// <summary>Hands <see cref="FailedCommands"/> the endpoint's session once there is one.</summary>
sealed class ScryCommandsFeature :
    Feature
{
    protected override void Setup(FeatureConfigurationContext context) =>
        context.RegisterStartupTask(new SessionStartup(context.Settings.Get<FailedCommands>()));

    sealed class SessionStartup(FailedCommands failed) :
        FeatureStartupTask
    {
        protected override Task OnStart(IMessageSession session, Cancel cancel = default)
        {
            failed.Use(session);
            return Task.CompletedTask;
        }

        protected override Task OnStop(IMessageSession session, Cancel cancel = default)
        {
            failed.Use(null);
            return Task.CompletedTask;
        }
    }
}
