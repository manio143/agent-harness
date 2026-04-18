using Microsoft.Extensions.AI;

namespace Agent.Server;

public interface IChatClientFactory
{
    IChatClient Get(string friendlyNameOrDefault);
}
