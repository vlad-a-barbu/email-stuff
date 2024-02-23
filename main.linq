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
	var configPath = args is null || args.Length < 1 ? "./config.json" : args[0];
	
	Directory.SetCurrentDirectory(Directory.GetParent(Util.CurrentQueryPath)!.FullName);
	var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;

	var credential = await GetCredentialAsync(config, new[] { GmailService.Scope.GmailReadonly });
	
	var service = new GmailService(new BaseClientService.Initializer{ HttpClientInitializer = credential });

	await foreach (var email in ReadEmails(service, config.EmailAddress))
	{
		email.Dump();
	}
}

record Config(string EmailAddress, string ClientId, string ClientSecret);

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

public record Email(string Date, string From, string Subject, List<string> Body);

static async IAsyncEnumerable<Email> ReadEmails(GmailService service, string emailAddress)
{
	var listMessagesRequest = service.Users.Messages.List(emailAddress);
	while (true)
	{
		var response = await listMessagesRequest.ExecuteAsync();
		foreach (var message in response.Messages)
		{
			var getMessageRequest = service.Users.Messages.Get(emailAddress, message.Id);
			getMessageRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
			var messageInfo = await getMessageRequest.ExecuteAsync();
			yield return messageInfo.ToEmail();
		}
		if (response.NextPageToken is null) break;
		listMessagesRequest.PageToken = response.NextPageToken;
	}
}

static class Extensions 
{
	public static Email ToEmail(this Message message)
	{
		var date = message.Payload.Headers.SingleOrDefault(h => h.Name == "Date")?.Value ?? string.Empty;
		var from = message.Payload.Headers.SingleOrDefault(h => h.Name == "From")?.Value ?? string.Empty;
		var subject = message.Payload.Headers.SingleOrDefault(h => h.Name == "Subject")?.Value ?? string.Empty;
		var encodedBodyChunks = !string.IsNullOrWhiteSpace(message.Payload.Body?.Data)
			? new List<string> { message.Payload.Body.Data }
			: message.Payload.Parts
				.Select(x => x.Body.Data)
				.Where(x => !string.IsNullOrWhiteSpace(x))
				.ToList();
		var body = encodedBodyChunks.Select(ParseBase64Chunk).ToList();
		return new Email(date, from, subject, body);
		
		static string ParseBase64Chunk(string chunk)
		{
			chunk = chunk.Replace("-", "+");
			chunk = chunk.Replace("_", "/");
			return Encoding.UTF8.GetString(Convert.FromBase64String(chunk));
		}
	}
}
