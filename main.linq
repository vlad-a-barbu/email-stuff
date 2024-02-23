<Query Kind="Program">
  <NuGetReference>Dapper</NuGetReference>
  <NuGetReference>Google.Apis.Gmail.v1</NuGetReference>
  <NuGetReference>Npgsql</NuGetReference>
  <Namespace>Dapper</Namespace>
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
  <Namespace>Microsoft.Extensions.DependencyInjection</Namespace>
  <Namespace>Microsoft.Extensions.DependencyInjection.Extensions</Namespace>
  <Namespace>Microsoft.Extensions.Logging</Namespace>
  <Namespace>Microsoft.Extensions.Logging.Abstractions</Namespace>
  <Namespace>Npgsql</Namespace>
  <Namespace>Npgsql.BackendMessages</Namespace>
  <Namespace>Npgsql.Internal</Namespace>
  <Namespace>Npgsql.Internal.Postgres</Namespace>
  <Namespace>Npgsql.NameTranslation</Namespace>
  <Namespace>Npgsql.PostgresTypes</Namespace>
  <Namespace>Npgsql.Replication</Namespace>
  <Namespace>Npgsql.Replication.Internal</Namespace>
  <Namespace>Npgsql.Replication.PgOutput</Namespace>
  <Namespace>Npgsql.Replication.PgOutput.Messages</Namespace>
  <Namespace>Npgsql.Replication.TestDecoding</Namespace>
  <Namespace>Npgsql.Schema</Namespace>
  <Namespace>Npgsql.TypeMapping</Namespace>
  <Namespace>Npgsql.Util</Namespace>
  <Namespace>NpgsqlTypes</Namespace>
  <Namespace>System.Management</Namespace>
  <Namespace>System.Text.Json</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

#nullable enable

async Task Main(string[] args)
{
	var configPath = args is null || args.Length < 1 ? "./config.json" : args[0];
	
	Directory.SetCurrentDirectory(Directory.GetParent(Util.CurrentQueryPath)!.FullName);
	var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;

	var credential = await GetCredentialAsync(config, new[] { GmailService.Scope.GmailReadonly });
	
	var service = new GmailService(new BaseClientService.Initializer{ HttpClientInitializer = credential });

	var filters = config.Filters.Select(CompileFilter).ToArray();

	using var db = new Db(config.ConnString);
	await foreach (var email in ReadEmails(service, config.EmailAddress, filters))
	{
		await db.StoreAsync(email);
	}
}

record Config(string EmailAddress, string ClientId, string ClientSecret, Filter[] Filters, string ConnString);
record Filter(string By, string Op, string[] Args);

static Func<Email, bool> CompileFilter(Filter filter)
{
	var prop = typeof(Email).GetProperty(filter.By)
		?? throw new ArgumentException(nameof(filter));

	return filter.Op.ToLower() switch
	{
		"eq" => (Email x) => prop.GetValue(x)!.ToString() == filter.Args.Single(),
		"neq" => (Email x) => prop.GetValue(x)!.ToString() != filter.Args.Single(),
		"in" => (Email x) => filter.Args.Contains(prop.GetValue(x)!.ToString()),
		
		_ => _ => true
	};
}

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

public record Email(string Id, DateTime Date, string From, string Subject, List<string> Body);

static async IAsyncEnumerable<Email> ReadEmails(
	GmailService service, string emailAddress,
	params Func<Email, bool>[] filters)
{
	var listMessagesRequest = service.Users.Messages.List(emailAddress);
	// todo -> map filters to ListRequest.Q
	while (true)
	{
		var response = await listMessagesRequest.ExecuteAsync();
		foreach (var message in response.Messages)
		{
			var getMessageRequest = service.Users.Messages.Get(emailAddress, message.Id);
			getMessageRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
			var messageInfo = await getMessageRequest.ExecuteAsync();
			var email = messageInfo.ToEmail();
			if (filters.All(f => f(email)))
			{
				yield return email;
			}
		}
		if (response.NextPageToken is null) break;
		listMessagesRequest.PageToken = response.NextPageToken;
	}
}

public static class Extensions
{
	public static Email ToEmail(this Message message)
	{
		var date = DateTimeOffset.FromUnixTimeMilliseconds(message.InternalDate!.Value).DateTime;
		var from = message.Payload.Headers.SingleOrDefault(h => h.Name == "From")?.Value ?? string.Empty;
		var fromEmail = Regex.Match(from, "<(.*)>").Groups[1].Value;
		var subject = message.Payload.Headers.SingleOrDefault(h => h.Name == "Subject")?.Value ?? string.Empty;
		var encodedBodyChunks = !string.IsNullOrWhiteSpace(message.Payload.Body?.Data)
			? new List<string> { message.Payload.Body.Data }
			: message.Payload.Parts
				.Select(x => x.Body.Data)
				.Where(x => !string.IsNullOrWhiteSpace(x))
				.ToList();
		var body = encodedBodyChunks.Select(ParseBase64Chunk).ToList();
		return new Email(message.Id, date, fromEmail, subject, body);
		
		static string ParseBase64Chunk(string chunk)
		{
			chunk = chunk.Replace("-", "+");
			chunk = chunk.Replace("_", "/");
			return Encoding.UTF8.GetString(Convert.FromBase64String(chunk));
		}
	}
}

class Db : IDisposable
{
	private readonly NpgsqlConnection _conn;

	public Db(string connString)
	{
		_conn = new NpgsqlConnection(connString);
		_conn.Open();
	}

	public async Task StoreAsync(Email email)
	{
		using var tran = await _conn.BeginTransactionAsync();
		try
		{
			await _conn.ExecuteAsync(InsertEmailSql(email));
			await tran.CommitAsync();
		}
		catch
		{
			await tran.RollbackAsync();
		}
	}

	private static string InsertEmailSql(Email email)
	{
		var sql = $@"
			INSERT INTO ""raw"".emails
			(""Id"", ""Date"", ""From"", ""Subject"", ""Body"")
			VALUES
			('{email.Id}', '{email.Date}', '{email.From}', '{email.Subject}',
			ARRAY [{string.Join(",", email.Body.Select(x => $"'{x.Replace("'", "''")}'"))}]);
		";
		return sql;
	}

	public void Dispose()
	{
		_conn.Close();
	}
}