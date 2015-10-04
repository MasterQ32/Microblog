using MarkdownSharp;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;

namespace Microblog
{
	class Program
	{
		static Markdown markdown = new Markdown(new MarkdownOptions()
		{
			AutoHyperlink = true,
			AutoNewlines = true,
		});

		static SQLiteConnection db;

		static void Main(string[] args)
		{
			db = new SQLiteConnection(ServerSettings.Default.ConnectionString);
			db.Open();

			CreateTables();

			bool running = true;
			Console.CancelKeyPress += (s, e) =>
			{
				running = false;
				e.Cancel = true;
			};

			Console.WriteLine("Press Ctrl+C to quit.");

			using (var http = new HttpListener())
			{
				http.Prefixes.Add(ServerSettings.Default.Prefix);
				http.Start();

				while (running)
				{
					HttpListenerContext context = null;
					try
					{
						var result = http.BeginGetContext(null, null);
						while (!result.IsCompleted)
						{
							if (running == false)
								goto quit;
							Thread.Sleep(0);
						}
						context = http.EndGetContext(result);
						switch (context.Request.HttpMethod)
						{
							case "POST":
								DispatchPost(context);
								break;
							default:
								DispatchGet(context);
								break;
						}

					}
					catch(Exception ex)
					{
						using (var sw = new StreamWriter(context.Response.OutputStream, Encoding.UTF8))
						{
							sw.Write(ex.ToString());
						}
						context.Response.StatusCode = 500;
					}
					finally
					{
						if (context != null)
							context.Response.Close();
					}
				}
			quit:
				db.Close();
			}
		}

		private static void DispatchPost(HttpListenerContext context)
		{
			var request = context.Request;
			var response = context.Response;

			string poster = request.Headers.Get("Post-User");
			string signature = request.Headers.Get("Post-Signature");

			if ((poster == null) || (signature == null))
				throw new InvalidOperationException("Invalid request.");

			byte[] payload;
			using (var ms = new MemoryStream())
			{
				request.InputStream.CopyTo(ms);
				payload = ms.ToArray();
			}

			string pubkey;
			{
				var cmd = db.CreateCommand();
				cmd.CommandText = @"SELECT `Pubkey` FROM `Writers` WHERE `Name`=@name;";
				cmd.Parameters.AddWithValue("@name", poster);
				pubkey = cmd.ExecuteScalar() as string;
			}

			RSAParameters args = new RSAParameters();
			args.Exponent = GetHexBytes(pubkey.Split('-')[0]);
			args.Modulus = GetHexBytes(pubkey.Split('-')[1]);

			if (VerifyData(payload, signature, args) == false)
			{
				response.StatusCode = 400;
				return;
			}

			int uid;
			{
				var cmd = db.CreateCommand();
				cmd.CommandText = @"SELECT `ID` FROM `Writers` WHERE `Name`=@name;";
				cmd.Parameters.AddWithValue("@name", poster);
				uid = (int)(long)cmd.ExecuteScalar();
			}

			WritePost(uid, Encoding.UTF8.GetString(payload));
		}

		private static void WritePost(int uid, string text)
		{
			var cmd = db.CreateCommand();
			cmd.CommandText = @"INSERT INTO `Entries` (`TimeStamp`, `Editor`, `Text`) VALUES (@timestamp, @editor, @text);";
			cmd.Parameters.AddWithValue("@timestamp", DateTime.Now.Ticks);
			cmd.Parameters.AddWithValue("@editor", uid);
			cmd.Parameters.AddWithValue("@text", text);
			cmd.ExecuteNonQuery();
		}

		static byte[] GetHexBytes(string text)
		{
			byte[] bits = new byte[text.Length / 2];
			for(int i = 0; i < bits.Length; i++)
			{
				bits[i] = Convert.ToByte(text.Substring(2 * i, 2), 16);
			}
			return bits;
		}

		public static bool VerifyData(byte[] bytesToVerify, string signedMessage, RSAParameters publicKey)
		{
			bool success = false;
			using (var rsa = new RSACryptoServiceProvider())
			{
				byte[] signedBytes = Convert.FromBase64String(signedMessage);
				try
				{
					rsa.ImportParameters(publicKey);

					SHA512Managed Hash = new SHA512Managed();

					byte[] hashedData = Hash.ComputeHash(signedBytes);

					success = rsa.VerifyData(bytesToVerify, CryptoConfig.MapNameToOID("SHA512"), signedBytes);
				}
				catch (CryptographicException e)
				{
					Console.WriteLine(e.Message);
				}
				finally
				{
					rsa.PersistKeyInCsp = false;
				}
			}
			return success;
		}

		private static void CreateTables()
		{
			string createEntriesTable = @"CREATE TABLE IF NOT EXISTS `Entries` (
				`ID`	INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
				`TimeStamp`	INTEGER NOT NULL,
				`Editor`	INTEGER DEFAULT 0,
				`Text`	TEXT
			);";
			string createWritersTable = @"CREATE TABLE IF NOT EXISTS `Writers` (
				`ID`		INTEGER PRIMARY KEY AUTOINCREMENT,
				`Name`		TEXT NOT NULL,
				`Mail`		TEXT,
				`Pubkey`	TEXT
			);";

			CreateTable(createEntriesTable);
			CreateTable(createWritersTable);
		}

		static void CreateTable(string command)
		{
			var cmd = db.CreateCommand();
			cmd.CommandText = command;
			cmd.ExecuteNonQuery();
		}

		private static void DispatchGet(HttpListenerContext context)
		{
			var request = context.Request;
			var response = context.Response;

			response.ContentType = "text/html";

			var query = HttpUtility.ParseQueryString(request.Url.Query);

			int startPost = 0;
			int numPosts = ServerSettings.Default.NumberOfPostsPerPage;
			int postID = 0;
			int editorID = 0;
			int currentPage = 1;

			if(query["page"] != null)
			{
				int page;
				if (int.TryParse(query["page"], out page))
				{
					numPosts = ServerSettings.Default.NumberOfPostsPerPage;
					startPost = page * numPosts;
					currentPage = page + 1;
				}
			}
			if (query["post"] != null)
			{
				int.TryParse(query["post"], out postID);
				currentPage = 0;

			}
			if(query["editor"] != null)
			{
				int.TryParse(query["editor"], out editorID);
				currentPage = 0;
			}

			using (var sw = new StreamWriter(response.OutputStream, Encoding.UTF8))
			{
				var indexPage = new IndexPage(markdown);
				indexPage.CurrentPage = currentPage;
				indexPage.NumberOfPages = (int)Math.Floor((double)GetEntryCount() / ServerSettings.Default.NumberOfPostsPerPage - 0.0001) + 1;
				{
					using (var cmd = db.CreateCommand())
					{
						cmd.CommandText =
							@"SELECT 
								`Entries`.`ID`, `Entries`.`Text`, `Entries`.`TimeStamp`, `Writers`.`Name`
							FROM
								`Entries`,`Writers`
							WHERE
								`Entries`.`Editor`=`Writers`.`ID`" + 
							(postID != 0 ? "AND `Entries`.`ID` = @postId \n" : "") +
							(editorID != 0 ? "AND `Entries`.`Editor` = @editorId \n" : "") +
							@" ORDER BY `Entries`.`TimeStamp` DESC
							LIMIT @start, @count";
						cmd.Parameters.AddWithValue("@start", startPost);
						cmd.Parameters.AddWithValue("@count", numPosts);
						cmd.Parameters.AddWithValue("@editorId", editorID);
						cmd.Parameters.AddWithValue("@postId", postID);
						using (var reader = cmd.ExecuteReader())
						{
							while (reader.Read())
							{
								var entry = new BlogEntry();
								entry.ID = reader.GetInt32(0);
								entry.Text = reader.GetString(1);
								entry.CreationDate = DateTime.FromBinary(reader.GetInt64(2));
								entry.Author = reader.GetString(3);
								indexPage.Entries.Add(entry);
							}
						}
					}
				}

				sw.Write(indexPage.TransformText());
				sw.Flush();
			}

		}

		static int GetEntryCount()
		{
			var cmd = db.CreateCommand();
			cmd.CommandText = "SELECT Count(*) FROM `Entries`";
			return (int)(long)cmd.ExecuteScalar();
		}
	}

	partial class IndexPage
	{
		private readonly Markdown markdown;

		public IndexPage() : this(new Markdown())
		{

		}

		public IndexPage(Markdown markdown)
		{
			this.markdown = markdown;
		}

		public int NumberOfPages { get; set; } = 1;

		public int CurrentPage { get; set; } = 0;

		public IList<BlogEntry> Entries { get; set; } = new List<BlogEntry>();
	}

	public class BlogEntry
	{
		public DateTime CreationDate { get; set; } = DateTime.Now;
		public string Author { get; set; } = "unknown";
		public string Text { get; set; } = "";
		public int ID { get; set; } = 0;
	}
}
