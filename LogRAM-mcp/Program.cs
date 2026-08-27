using LogRAM.Mcp;

if (args is ["--self-test"])
{
    return await McpServer.SelfTestAsync();
}

using var server = new McpServer();
return await server.RunAsync();
