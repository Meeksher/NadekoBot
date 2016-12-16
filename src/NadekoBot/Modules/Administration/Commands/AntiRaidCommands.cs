﻿using Discord;
using Discord.Commands;
using NadekoBot.Attributes;
using NadekoBot.Extensions;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace NadekoBot.Modules.Administration
{
    public partial class Administration
    {
        public enum PunishmentAction
        {
            Mute,
            Kick,
            Ban,
        }

        public enum ProtectionType
        {
            Raiding,
            Spamming,
        }

        private class AntiRaidSetting
        {
            public int UserThreshold { get; set; }
            public int Seconds { get; set; }
            public PunishmentAction Action { get; set; }
            public int UsersCount { get; set; }
            public ConcurrentHashSet<IGuildUser> RaidUsers { get; set; } = new ConcurrentHashSet<IGuildUser>();
        }

        private class AntiSpamSetting
        {
            public PunishmentAction Action { get; set; }
            public int MessageThreshold { get; set; } = 3;
            public ConcurrentDictionary<ulong, UserSpamStats> UserStats { get; set; }
                = new ConcurrentDictionary<ulong, UserSpamStats>();
        }

        private class UserSpamStats
        {
            public int Count { get; set; }
            public string LastMessage { get; set; }

            public UserSpamStats(string msg)
            {
                Count = 1;
                LastMessage = msg.ToUpperInvariant();
            }

            public void ApplyNextMessage(string message)
            {
                var upperMsg = message.ToUpperInvariant();
                if (upperMsg == LastMessage)
                    Count++;
                else
                {
                    LastMessage = upperMsg;
                    Count = 0;
                }
            }
        }

        [Group]
        public class AntiRaidCommands
        {
            private static ConcurrentDictionary<ulong, AntiRaidSetting> antiRaidGuilds =
                    new ConcurrentDictionary<ulong, AntiRaidSetting>();
            // guildId | (userId|messages)
            private static ConcurrentDictionary<ulong, AntiSpamSetting> antiSpamGuilds =
                    new ConcurrentDictionary<ulong, AntiSpamSetting>();

            private static Logger _log { get; }

            static AntiRaidCommands()
            {
                _log = LogManager.GetCurrentClassLogger();

                NadekoBot.Client.MessageReceived += (imsg) =>
                {
                    var msg = imsg as IUserMessage;
                    if (msg == null || Context.User.IsBot)
                        return Task.CompletedTask;

                    //var channel = Context.Channel as ITextChannel;
                    if (channel == null)
                        return Task.CompletedTask;

                    var t = Task.Run(async () =>
                    {
                        try
                        {
                            AntiSpamSetting spamSettings;
                            if (!antiSpamGuilds.TryGetValue(Context.Guild.Id, out spamSettings))
                                return;

                            var stats = spamSettings.UserStats.AddOrUpdate(Context.User.Id, new UserSpamStats(msg.Content),
                                (id, old) => { old.ApplyNextMessage(msg.Content); return old; });

                            if (stats.Count >= spamSettings.MessageThreshold)
                            {
                                if (spamSettings.UserStats.TryRemove(Context.User.Id, out stats))
                                {
                                    await PunishUsers(spamSettings.Action, ProtectionType.Spamming, (IGuildUser)Context.User)
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                        catch { }
                    });
                    return Task.CompletedTask;
                };

                NadekoBot.Client.UserJoined += (usr) =>
                {
                    if (usr.IsBot)
                        return Task.CompletedTask;

                    AntiRaidSetting settings;
                    if (!antiRaidGuilds.TryGetValue(usr.Guild.Id, out settings))
                        return Task.CompletedTask;

                    var t = Task.Run(async () =>
                    {
                        if (!settings.RaidUsers.Add(usr))
                            return;

                        ++settings.UsersCount;

                        if (settings.UsersCount >= settings.UserThreshold)
                        {
                            var users = settings.RaidUsers.ToArray();
                            settings.RaidUsers.Clear();

                            await PunishUsers(settings.Action, ProtectionType.Raiding, users).ConfigureAwait(false);
                        }
                        await Task.Delay(1000 * settings.Seconds).ConfigureAwait(false);

                        settings.RaidUsers.TryRemove(usr);
                        --settings.UsersCount;
                    });

                    return Task.CompletedTask;
                };
            }

            private static async Task PunishUsers(PunishmentAction action, ProtectionType pt, params IGuildUser[] gus)
            {
                foreach (var gu in gus)
                {
                    switch (action)
                    {
                        case PunishmentAction.Mute:
                            try
                            {
                                await MuteCommands.Mute(gu).ConfigureAwait(false);
                            }
                            catch (Exception ex) { _log.Warn(ex, "I can't apply punishement"); }
                            break;
                        case PunishmentAction.Kick:
                            try
                            {
                                await gu.Guild.AddBanAsync(gu, 7).ConfigureAwait(false);
                                try
                                {
                                    await gu.Guild.RemoveBanAsync(gu).ConfigureAwait(false);
                                }
                                catch
                                {
                                    await gu.Guild.RemoveBanAsync(gu).ConfigureAwait(false);
                                    // try it twice, really don't want to ban user if 
                                    // only kick has been specified as the punishement
                                }
                            }
                            catch (Exception ex) { _log.Warn(ex, "I can't apply punishment"); }
                            break;
                        case PunishmentAction.Ban:
                            try
                            {
                                await gu.Guild.AddBanAsync(gu, 7).ConfigureAwait(false);
                            }
                            catch (Exception ex) { _log.Warn(ex, "I can't apply punishment"); }
                            break;
                        default:
                            break;
                    }
                }
                await LogCommands.TriggeredAntiProtection(gus, action, pt).ConfigureAwait(false);
            }


            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.Administrator)]
            public async Task AntiRaid(IUserMessage imsg, int userThreshold, int seconds, PunishmentAction action)
            {
                ////var channel = (ITextChannel)Context.Channel;

                if (userThreshold < 2 || userThreshold > 30)
                {
                    await Context.Channel.SendErrorAsync("❗️User threshold must be between **2** and **30**.").ConfigureAwait(false);
                    return;
                }

                if (seconds < 2 || seconds > 300)
                {
                    await Context.Channel.SendErrorAsync("❗️Time must be between **2** and **300** seconds.").ConfigureAwait(false);
                    return;
                }

                try
                {
                    await MuteCommands.GetMuteRole(Context.Guild).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await Context.Channel.SendConfirmAsync("⚠️ Failed creating a mute role. Give me ManageRoles permission" +
                        "or create 'nadeko-mute' role with disabled SendMessages and try again.")
                            .ConfigureAwait(false);
                    _log.Warn(ex);
                    return;
                }

                var setting = new AntiRaidSetting()
                {
                    Action = action,
                    Seconds = seconds,
                    UserThreshold = userThreshold,
                };
                antiRaidGuilds.AddOrUpdate(Context.Guild.Id, setting, (id, old) => setting);

                await Context.Channel.SendConfirmAsync($"ℹ️ {Context.User.Mention} If **{userThreshold}** or more users join within **{seconds}** seconds, I will **{action}** them.")
                        .ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.Administrator)]
            public async Task AntiSpam(IUserMessage imsg, int messageCount=3, PunishmentAction action = PunishmentAction.Mute)
            {
                ////var channel = (ITextChannel)Context.Channel;

                if (messageCount < 2 || messageCount > 10)
                    return;

                AntiSpamSetting throwaway;
                if (antiSpamGuilds.TryRemove(Context.Guild.Id, out throwaway))
                {
                    await Context.Channel.SendConfirmAsync("🆗 **Anti-Spam feature** has been **disabled** on this server.").ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await MuteCommands.GetMuteRole(Context.Guild).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await Context.Channel.SendErrorAsync("⚠️ Failed creating a mute role. Give me ManageRoles permission" +
                            "or create 'nadeko-mute' role with disabled SendMessages and try again.")
                                .ConfigureAwait(false);
                        _log.Warn(ex);
                        return;
                    }

                    if (antiSpamGuilds.TryAdd(Context.Guild.Id, new AntiSpamSetting()
                    {
                        Action = action,
                        MessageThreshold = messageCount,
                    }))
                    await Context.Channel.SendConfirmAsync("✅ **Anti-Spam feature** has been **enabled** on this server.").ConfigureAwait(false);
                }

            }
        }
    }
}
