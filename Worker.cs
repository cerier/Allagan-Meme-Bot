using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

/*
 * Discord Bot for storing & retrieving memes in a text format.
 * Alex Cerier, 12/9/2021
 *
 * Credits to Niels Swimberghe for the very good .Net Core / Container / Azure 'how-to' on:
 * https://swimburger.net/blog/azure/how-to-create-a-discord-bot-using-the-dotnet-worker-template-and-host-it-on-azure-container-instances
 *
 */

namespace DiscordBot
{
    public class Worker : BackgroundService
    {
        private ILogger<Worker> logger;
        private IConfiguration configuration;
        private DiscordClient discordClient;

        private static string botVersion = "2021.12.10.3";
        private static string connString;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            this.logger = logger;
            this.configuration = configuration;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                logger.LogInformation("Starting discord bot (StartAsync)"); 

                // Get settings from the configuration. 
                string discordBotToken = configuration["DiscordBotToken"];
                connString = configuration["BackendConnectionString"];

                // Set up the Discord client. 
                discordClient = new DiscordClient(new DiscordConfiguration()
                {
                    Token = discordBotToken,
                    TokenType = TokenType.Bot,
                    Intents = DiscordIntents.AllUnprivileged
                });

                discordClient.MessageCreated += OnMessageCreated;
                await discordClient.ConnectAsync();
            } 
            catch (Exception ex)
            {
                logger.LogError(ex.Message);
            }
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            discordClient.MessageCreated -= OnMessageCreated;
            await discordClient.DisconnectAsync();
            discordClient.Dispose();
            logger.LogInformation("Discord bot stopped");
        }

        // The 'main' bit of the program that determines which actions to take based on contents of messages. 
        private async Task OnMessageCreated(DiscordClient client, MessageCreateEventArgs e)
        {
            // Make sure to ignore messages created by this bot (efficiency!)
            string messageAuthor = e.Author.Username.ToString();
            if (messageAuthor == "Allagan Meme Bot")
            {
                return;
            }

            // When the user wants a meme by a specific ID
            if (e.Message.Content.StartsWith("!meme get id", StringComparison.OrdinalIgnoreCase))
            {
                int memeId = Convert.ToInt32(e.Message.Content.Replace("!meme get id", "").Trim());
                await e.Message.RespondAsync(GetMeme("", memeId));
                logger.LogInformation("User fetched a meme");
            }

            // When the user wants a meme by searching text
            else if (e.Message.Content.StartsWith("!meme get", StringComparison.OrdinalIgnoreCase))
            {
                string searchText = e.Message.Content.Replace("!meme get", "").Trim();
                await e.Message.RespondAsync( GetMeme(searchText, -1) );
                logger.LogInformation("User fetched a meme");
            } 
            
            // when the user wants to add a meme
            else if (e.Message.Content.StartsWith("!meme insert", StringComparison.OrdinalIgnoreCase) || e.Message.Content.StartsWith("!meme add", StringComparison.OrdinalIgnoreCase))
            {
                string memeToInsert = e.Message.Content.Replace("!meme insert", "").Replace("!meme add", "").Trim();
                await e.Message.RespondAsync( InsertMeme(memeToInsert, messageAuthor) );
                logger.LogInformation("User inserted a meme");
            }

            // When the user has entered an invalid/unknown command for this bot. 
            else if (e.Message.Content.StartsWith("!meme", StringComparison.OrdinalIgnoreCase))
            {
                string helpInfo = "<Bleep, Bloop>. My purpose is to archive memes. "
                                + "```!meme add <text>  --  Adds a meme to the archive. "
                                + "\n!meme get <text>  --  Searches memes and returns up to five. "
                                + "\n!meme get id <#>  --  Gets a specific meme if you know its ID.```"
                                + "Please note, some text in ffxiv is 'custom' and can be displayed only in ffxiv.  Such text will be removed by this bot because Discord (and other programs) cannot show it."
                                + "\nVersion " + botVersion + ", by @Alex B";
                await e.Message.RespondAsync(helpInfo);
                logger.LogInformation("User fetched help info");
            }
        }

        // Removes characters that Discord can't show.
        // Specifically it removes 'private' characters, IE, chars used by ffxiv and nothing else. 
        private static string CleanStringForDiscord(string text)
        { 
            text = Regex.Replace(text, @"[\uE000-\uF8FF]", ""); // https://en.wikipedia.org/wiki/Private_Use_Areas 
            return text;
        }

        // Inserts a meme into storage.  The back-end logic is start enough to not insert duplicates.
        // The function returns the 'conclusion' (whether the meme was inserted, was already present)
        private static string InsertMeme(string newMeme, string author)
        {
            int wasInserted = -1, memeId = -1;
            using (SqlConnection con = new SqlConnection(connString))
            {
                string spName = "[dbo].[Insert_One_Meme]";
                using (SqlCommand cmd = new SqlCommand(spName))
                {
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.Add(new SqlParameter("@MemeText", CleanStringForDiscord(newMeme) ));
                    cmd.Parameters.Add(new SqlParameter("@MemeAddedBy", author));
                    cmd.Parameters.Add(new SqlParameter("@MemeAddedOn", DateTime.UtcNow));
                    cmd.Connection = con;
                    con.Open();

                    using (SqlDataReader sdr = cmd.ExecuteReader())
                    {
                        while (sdr.Read())
                        {
                            wasInserted = Convert.ToInt32(sdr["MemeWasInserted"]);
                            memeId = Convert.ToInt32(sdr["MemeId"]);
                        }
                    }
                    con.Close();
                }
            }

            if (wasInserted == 1)
            {
                return "New meme registered (ID " + memeId + ")";
            }
            if (wasInserted == 0)
            {
                return "The meme was already registered (ID " + memeId + ")";
            }
            return "Not sure if the meme was inserted (does the code not handle a new SP return value?)";
        }

        // Returns up to a certain number of memes that match the search criteria. 
        private static string GetMeme(string searchText, int specificMemeId)
        {
            string allMemes = "";
            string memeEntry, memeText, memeAddedBy, memeId;
            DateTime memeAddedOn; 

            using (SqlConnection con = new SqlConnection(connString))
            {
                string spName = "[dbo].[Get_Memes_By_Text_MemeId]";
                using (SqlCommand cmd = new SqlCommand(spName))
                {
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.Add(new SqlParameter("@SearchText", searchText));
                    cmd.Parameters.Add(new SqlParameter("@MemeId", specificMemeId));
                    cmd.Connection = con;
                    con.Open();
                    using (SqlDataReader sdr = cmd.ExecuteReader())
                    {
                        while (sdr.Read())
                        {
                            memeId = sdr["MemeId"].ToString();
                            memeText = sdr["MemeText"].ToString();
                            memeAddedBy = sdr["MemeAddedBy"].ToString();
                            memeAddedOn = DateTime.Parse(sdr["MemeAddedOn"].ToString());

                            // Build a nicely formatted output for this one meme and add to the list of returned memes.
                            string daysAgo = ( (int)Math.Floor( (DateTime.UtcNow - memeAddedOn).TotalDays )).ToString();
                            memeEntry = "```" // Adds a nice 'block' around the text. 
                                      + memeText 
                                      + "```" 
                                      + "Added "+daysAgo+" days ago by "+memeAddedBy+". ID = "+memeId+"\n\n";
                            allMemes += memeEntry;
                        }
                    }
                    con.Close();
                }
            }

            if (allMemes.Length > 1900) // Discord permits messages upto 2000 characters. 
            {
                return allMemes.Substring(0,1900) + "\n\n **WARNING: Maximum Discord message length 2000 reached, output truncated!**";
            }
            if (allMemes.Length == 0) // If nothing was found return a hint.
            {
                return "None found.";
            }
            return allMemes;
        }
    }
}