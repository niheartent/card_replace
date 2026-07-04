namespace CardReplace.Scripts.ArtPacks;

public interface IModLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);
}

public sealed class DelegateModLog(Action<string> info, Action<string> warn, Action<string> error) : IModLog
{
    public void Info(string message) => info(message);

    public void Warn(string message) => warn(message);

    public void Error(string message) => error(message);
}
