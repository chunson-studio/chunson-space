namespace ChunsonSpace.Desktop;

internal static class TwoStageLoginVerification
{
    public static async Task ContinueAsync(ShellViewModel model, DesktopAccountServices services, Task operation)
    {
        for (var attempt = 0; attempt < 500 && !operation.IsCompleted; attempt++)
        {
            if (services.Login.Status.Stage == Accounts.LoginStage.AccountPreparation)
            {
                if (services.Browser is OfflineLoginBrowser existing) existing.PreparationClosed = true;
                if (services.OfflineNewAccountBrowser is { } fresh) fresh.PreparationClosed = true;
            }
            if (services.Login.CanBeginAuthorization && model.ResumeLoginCommand.CanExecute(null))
            { model.ResumeLoginCommand.Execute(null); return; }
            await Task.Delay(10);
        }
        if (!operation.IsCompleted) throw new InvalidOperationException("Fixture did not reach account preparation.");
    }
}
