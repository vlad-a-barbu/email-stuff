<Query Kind="Program">
  <NuGetReference>Google.Apis.Gmail.v1</NuGetReference>
  <Namespace>Google</Namespace>
  <Namespace>Google.Apis</Namespace>
  <Namespace>Google.Apis.Auth</Namespace>
  <Namespace>Google.Apis.Auth.OAuth2</Namespace>
  <Namespace>Google.Apis.Auth.OAuth2.Flows</Namespace>
  <Namespace>Google.Apis.Auth.OAuth2.Requests</Namespace>
  <Namespace>Google.Apis.Auth.OAuth2.Responses</Namespace>
  <Namespace>Google.Apis.Auth.OAuth2.Web</Namespace>
  <Namespace>Google.Apis.Discovery</Namespace>
  <Namespace>Google.Apis.Download</Namespace>
  <Namespace>Google.Apis.Gmail.v1</Namespace>
  <Namespace>Google.Apis.Gmail.v1.Data</Namespace>
  <Namespace>Google.Apis.Http</Namespace>
  <Namespace>Google.Apis.Json</Namespace>
  <Namespace>Google.Apis.Logging</Namespace>
  <Namespace>Google.Apis.Requests</Namespace>
  <Namespace>Google.Apis.Requests.Parameters</Namespace>
  <Namespace>Google.Apis.Services</Namespace>
  <Namespace>Google.Apis.Testing</Namespace>
  <Namespace>Google.Apis.Upload</Namespace>
  <Namespace>Google.Apis.Util</Namespace>
  <Namespace>Google.Apis.Util.Store</Namespace>
  <Namespace>System.Management</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
  <Namespace>System.Text.Json</Namespace>
</Query>

#nullable enable

async Task Main(string[] args)
{
#if DEBUG
	var configPath = "./config.json";
#else
	if (args.Length < 1) return;
	var configPath = args[0];
#endif
	Directory.SetCurrentDirectory(Directory.GetParent(Util.CurrentQueryPath)!.FullName);
	var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;

	var credential = await GetCredentialAsync(config, new[] { GmailService.Scope.GmailReadonly });
	
	var service = new GmailService(new BaseClientService.Initializer
	{
		HttpClientInitializer = credential
	});

	var emails = new List<Email>();
	var listMessagesRequest = service.Users.Messages.List(config.EmailAddress);
	while (true)
	{
		var response = await listMessagesRequest.ExecuteAsync();
		response.Messages.Count().Dump();
		foreach (var message in response.Messages)
		{
			var getMessageRequest = service.Users.Messages.Get(config.EmailAddress, message.Id);
			getMessageRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
			var messageInfo = await getMessageRequest.ExecuteAsync();
			emails.Add(messageInfo.ToEmail());
		}
#if DEBUG
		break;
#else
		if (response.NextPageToken is null) break;
		listMessagesRequest.PageToken = response.NextPageToken;
#endif
	}
	emails.Dump();
}

public record Email(string Date, string From, string Subject, string Body);

static async Task<UserCredential> GetCredentialAsync(Config config, string[] scopes)
{
	var secrets = new ClientSecrets
	{
		ClientId = config.ClientId,
		ClientSecret = config.ClientSecret
	};
	
	return await GoogleWebAuthorizationBroker.AuthorizeAsync(
		secrets, scopes, 
		config.EmailAddress, 
		CancellationToken.None);
}

record Config(string EmailAddress, string ClientId, string ClientSecret);

static class Extensions 
{
	public static Email ToEmail(this Message message)
	{
		var date = message.Payload.Headers.SingleOrDefault(h => h.Name == "Date")?.Value ?? string.Empty;
		var from = message.Payload.Headers.SingleOrDefault(h => h.Name == "From")?.Value ?? string.Empty;
		var subject = message.Payload.Headers.SingleOrDefault(h => h.Name == "Subject")?.Value ?? string.Empty;
		var body = ""; // todo
		return new Email(date, from, subject, body);
	}
}