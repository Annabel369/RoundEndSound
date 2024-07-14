using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Menu;
using Dapper;
using MySqlConnector;
using RoundEndSound.Models;
using RoundEndSound.Repository;
using RoundEndSound.Services;
using static RoundEndSound.Repository.QueryConstants;
using RoundEndSound.Config;

namespace RoundEndSound
{
    [MinimumApiVersion(199)]
    public class RoundEndSound : BasePlugin, IPluginConfig<Config.Config>
    {
        public override string ModuleName => "Round End Sound";
        public override string ModuleVersion => "1.0.2";
        public override string ModuleAuthor => "gleb_khlebov";
        public override string ModuleDescription => "Plays a sound at the end of the round";
        
        public Config.Config Config { get; set; } = new();
        
        private static string? _connectionString;
        private static RepositoryService? _databaseService;
        
        private readonly Random _random = new();
        private readonly Utils.LogUtils _logUtils = new();
        private readonly Utils.PlayerUtils _playerUtils = new();
        
        private int _trackCount;
        private int _lastPlayedTrackIndex;
        private static Sound? _lastPlayedTrack;
        private static List<Sound> _tracks = [];
        private readonly HashSet<Sound> _playedSongs = [];
        
        private readonly HashSet<string> _playersHotLoaded = [];
        private readonly Dictionary<string, ResPlayer?> _players = [];
        private readonly Dictionary<string, ResPlayer?> _playersForSave = [];

        private const char NewLine = '\u2029';

        private bool shouldShowImage = false;

        public override void Load(bool hotReload)
        {
            base.Load(hotReload);

            Console.WriteLine(" ");
            Console.WriteLine("  _____                       _   ______           _    _____                       _ ");
            Console.WriteLine(" |  __ \\                     | | |  ____|         | |  / ____|                     | |");
            Console.WriteLine(" | |__) |___  _   _ _ __   __| | | |__   _ __   __| | | (___   ___  _   _ _ __   __| |");
            Console.WriteLine(" |  _  // _ \\| | | | '_ \\ / _` | |  __| | '_ \\ / _` |  \\___ \\ / _ \\| | | | '_ \\ / _` |");
            Console.WriteLine(" | | \\ \\ (_) | |_| | | | | (_| | | |____| | | | (_| |  ____) | (_) | |_| | | | | (_| |");
            Console.WriteLine(" |_|  \\_\\___/ \\__,_|_| |_|\\__,_| |______|_| |_|\\__,_| |_____/ \\___/ \\__,_|_| |_|\\__,_|");
            Console.WriteLine("                                                                                      ");
            Console.WriteLine("                                                                                      ");
            Console.WriteLine("			    >> Version: " + ModuleVersion);
            Console.WriteLine("			    >> Author: " + ModuleAuthor);
            Console.WriteLine(" ");

            if (hotReload)
            {
                UpdatePlayersAfterReload();
            }
            
            RegisterEventHandler<EventRoundMvp>((@event, info) =>
            {
                CCSPlayerController? player = @event.Userid;
            
                if (_playerUtils.IsInvalidPlayer(player))
                    return HookResult.Continue;

                if (_players.TryGetValue(player.SteamID.ToString(), out var user))
                {
                    if (!user!.SoundEnabled) return HookResult.Continue;
                }
                
                info.DontBroadcast = true;
            
                return HookResult.Continue;
            }, HookMode.Pre);
            
            AddCommand("css_res", "Command that opens the Round End Sound menu",
                (player, _) => CreateMenu(player));
        }
        
        public override void Unload(bool hotReload)
        {
            base.Unload(hotReload);
            
            _tracks.Clear();
            _players.Clear();
            _playedSongs.Clear();
            _playersForSave.Clear();
            _playersHotLoaded.Clear();

            _lastPlayedTrack = null;
        }
        
        public void OnConfigParsed(Config.Config config)
        {
            Database dbConfig = config.DbConfig;
            
            if (dbConfig.Host.Length < 1 || dbConfig.DbName.Length < 1 || dbConfig.User.Length < 1)
            {
                _logUtils.Log("You need to setup Database credentials in config!");
                throw new Exception("[Round End Sound] You need to setup Database credentials in config!");
            }

            var builder = new MySqlConnectionStringBuilder
            {
                Server = dbConfig.Host,
                UserID = dbConfig.User,
                Password = dbConfig.Password,
                Database = dbConfig.DbName,
                Port = (uint)dbConfig.Port,
                Pooling = true
            };

            _connectionString = builder.ConnectionString;

            _databaseService = new RepositoryService(_connectionString);
            Task.Run(() => _databaseService.CreateTable());
            
            _tracks = config.MusicList;
            _trackCount = _tracks.Count;
            Config = config;
        }
        
        [GameEventHandler]
        public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
        {
            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            CCSPlayerController? player = @event.Userid;
            
            if (_playerUtils.IsInvalidPlayer(player))
                return HookResult.Continue;

            ResPlayer resPlayer = new ResPlayer
            {
                SteamId = player.SteamID.ToString(),
                SoundEnabled = Config.DefaultEnableMusic,
                ChatEnabled = Config.DefaultEnableNotify,
            };
            
            Task.Run(() => GetPlayerAsync(resPlayer, currentTime));
            
            return HookResult.Continue;
        }
        
        [GameEventHandler]
        public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Userid;
            string steamId = player.SteamID.ToString();
            
            if (_playerUtils.IsInvalidPlayer(player))
                return HookResult.Continue;

            if (!_players.ContainsKey(steamId))
                return HookResult.Continue;

            if (_playersForSave.TryGetValue(steamId, out ResPlayer? value))
            {
                Task.Run(() => SavePlayersAsync([value]));
                _playersForSave.Remove(steamId);
            }
            
            _players.Remove(steamId);

            return HookResult.Continue;
        }
        
        [GameEventHandler]
        public HookResult OnServerShutdown(EventServerShutdown @event, GameEventInfo info)
        {
            if (_playersForSave.Count < 1)
                return HookResult.Continue;
            
            Task.Run(() => SavePlayersAsync(_playersForSave.Values.ToList()));
            
            return HookResult.Continue;
        }

        [GameEventHandler]
        public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
        {
            if (_players.Count < 1)
                return HookResult.Continue;

            if (_trackCount < 1)
                return HookResult.Continue;

            int trackIndex;
            Sound currentSound;

            if (Config.RandomSelectionMode) {
                if (_playedSongs.Count == _trackCount)
                    _playedSongs.Clear();
            
                do
                {
                    trackIndex = _random.Next(_trackCount);
                    currentSound = _tracks[trackIndex];
                } while (_playedSongs.Contains(currentSound));
            
                _playedSongs.Add(currentSound);
            }
            else
            {
                if (_lastPlayedTrack == null || _lastPlayedTrackIndex.Equals(_tracks.Count-1))
                {
                    trackIndex = 0;
                }
                else
                {
                    trackIndex = _lastPlayedTrackIndex + 1;
                }
                currentSound = _tracks[trackIndex];
                _lastPlayedTrackIndex = trackIndex;
            }
            
            foreach (var player in _players.Select(resPlayer => resPlayer.Value))
            {
                PlaySound(player, currentSound);
            }

            _lastPlayedTrack = currentSound;

            return HookResult.Continue;
        }

        public class MusicList
        {
            public string Name { get; set; } = "Amarillo";
            public string Music { get; set; } = "sounds/marius_music/amarillo.vsnd_c";
            public string Gif { get; set; } = "https://c.tenor.com/nEu74vu_sT4AAAAC/tenor.gif";
        }

        public List<MusicList> music_list { get; set; } = new()
        {
             new MusicList { Name = "Amarillo", Music = "sounds/marius_music/amarillo.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/12/baby-yoda-02.gif" },
             new MusicList { Name = "marius2", Music = "sounds/marius_music/aaaaaadao.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2021/10/gif-naruto-fofo-03.gif" },
             new MusicList { Name = "marius3", Music = "sounds/marius_music/actafool.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2021/10/gif-naruto-fofo-15.gif" },
             new MusicList { Name = "marius4", Music = "sounds/marius_music/addicted.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2021/10/gif-naruto-fofo-19.gif" },
             new MusicList { Name = "marius5", Music = "sounds/marius_music/adin_ross_she_make_it_clap.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/06/eu-te-amo.gif" },
             new MusicList { Name = "marius6", Music = "sounds/marius_music/albertnbn_zoro.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/06/deadpool-08.gif" },
             //new MusicList { Name = "marius7", Music = "path5", Gif = "link5" },
             //new MusicList { Name = "marius8", Music = "path5", Gif = "link5" },
             //new MusicList { Name = "marius9", Music = "path5", Gif = "link5" },
             new MusicList { Name = "marius10", Music = "sounds/marius_music/damy.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/06/pets-comendo-bobagem.gif" },
             //new MusicList { Name = "marius11", Music = "path5", Gif = "link5" },
             new MusicList { Name = "marius12", Music = "sounds/marius_music/habibi.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/06/pet-bebendo-agua-da-privada.gif" },
             new MusicList { Name = "marius13", Music = "sounds/marius_music/marius.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/06/gato-legal-fofo-07.gif" },
             new MusicList { Name = "marius14", Music = "sounds/marius_music/owneri.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/06/crianca-assustadora-07.gif" },
             //new MusicList { Name = "marius15", Music = "path5", Gif = "link5" },
             new MusicList { Name = "marius16", Music = "sounds/marius_music/saize.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/06/crianca-assustadora-06.gif" },
             new MusicList { Name = "marius17", Music = "sounds/marius_music/sava.vsnd_c", Gif = "https://gifman.net/wp-content/uploads/2019/06/crianca-assustadora-04.gif" }
        };

        private void PlaySound(ResPlayer? resPlayer, Sound sound)
        {
            CCSPlayerController? player = Utils.PlayerUtils.GetPlayerFromSteamId(resPlayer!.SteamId);

            var thirdItem = music_list[2];
            
            Server.NextFrame(() =>
            {   
                
                    //Console.WriteLine($"Music: {item.Music}, Gif: {item.Gif}");
                    if (resPlayer.SoundEnabled)
                    player?.ExecuteClientCommand($"play {sound.Path}");

                    if (resPlayer.ChatEnabled)
                    {
                        player?.PrintToChat($"{Localizer["chat.Prefix"]}{Localizer["chat.PlayedSong", sound.Name]}{NewLine}{Localizer["chat.Settings"]}");

                        

                        if (sound.Name=="Amarillo")
                        {
                            thirdItem = music_list[0];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius2")
                        {
                            thirdItem = music_list[1];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius3")
                        {
                            thirdItem = music_list[2];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius4")
                        {
                            thirdItem = music_list[3];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius5")
                        {
                            thirdItem = music_list[4];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius6")
                        {
                            thirdItem = music_list[5];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius10")
                        {
                            thirdItem = music_list[6];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius12")
                        {
                            thirdItem = music_list[7];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius13")
                        {
                            thirdItem = music_list[8];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius14")
                        {
                            thirdItem = music_list[9];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius16")
                        {
                            thirdItem = music_list[10];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        if (sound.Name=="marius17")
                        {
                            thirdItem = music_list[11];
                            Globals.SiteImage = thirdItem.Gif;
                        }

                        RegisterListener<Listeners.OnTick>(OnTick);
                        
                        shouldShowImage = true;
                        
                        AddTimer(10, () =>
                        {
                            shouldShowImage = false;
                        });
                    }
                
                
            });
        }
        
        private void PlayLastSound(CCSPlayerController player)
        {
            Server.NextFrame(() =>
            {   
                player.ExecuteClientCommand($"play {_lastPlayedTrack!.Path}");
                player.PrintToChat($"{Localizer["chat.Prefix"]}{Localizer["chat.PlayedSong", _lastPlayedTrack.Name]}{NewLine}{Localizer["chat.Settings"]}");
            });
        }

    public void OnTick()
    {    
        string gifUrl = Globals.SiteImage;

        if (shouldShowImage)
        {
            foreach (CCSPlayerController player in Utilities.GetPlayers())
            {
                if (player != null && player.IsValid)
                {
                    player.PrintToCenterHtml($"<img src=\"{gifUrl}\">",10);
                }   
            }
        }
    }
        
        private async Task GetPlayerAsync(ResPlayer? resPlayer, long currentTime)
        {
            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();
                
                await connection.ExecuteAsync(InsertPlayer,
                    new { SteamID = resPlayer?.SteamId, resPlayer?.SoundEnabled, 
                        resPlayer?.ChatEnabled, LastConnect = currentTime });
                
                var playerData = await connection.QuerySingleOrDefaultAsync<ResPlayer>(
                    SelectPlayer, new {resPlayer?.SteamId});
                
                if (playerData == null)
                    return;
                
                InsertPlayerData(playerData); 
            }
            catch (Exception ex)
            {
                _logUtils.Log($"Error in GetPlayerAsync: {ex.Message}");
            }
        }
        
        private async Task GetPlayersAsync(HashSet<string> players)
        {
            if (players.Count < 1) return;
            
            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();
                var playersData = connection.QueryAsync<ResPlayer>(SelectPlayers,
                    new {players}).Result.ToList();

                foreach (var resPlayer in playersData)
                {
                    InsertPlayerData(resPlayer);
                } 
            }
            catch (Exception ex)
            {
                _logUtils.Log($"Error in GetPlayersAsync: {ex.Message}");
            }
        }
        
        private async Task SavePlayersAsync(List<ResPlayer?> players)
        {
            if (players.Count < 1) return;
            
            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync(UpdatePlayer, players);
            }
            catch (Exception ex)
            {
                _logUtils.Log($"Error in SavePlayersAsync: {ex.Message}");
            }
        }
        
        private void InsertPlayerData(ResPlayer resPlayer)
        {
            Server.NextFrame(() =>
            {
                _players[resPlayer.SteamId!] = resPlayer;
            });    
        }

        private void UpdatePlayersAfterReload()
        {
            foreach (var player in Utils.PlayerUtils.GetOnlinePlayers())
            {
                _playersHotLoaded.Add(player.SteamID.ToString());
            }

            Task.Run(() => GetPlayersAsync(_playersHotLoaded));
        }
        
        private void CreateMenu(CCSPlayerController? player)
        {
            if (player == null) return;

            string steamId = player.SteamID.ToString();

            if (!_players.TryGetValue(steamId, out var user)) return;

            bool lastPlayedTrackIsNull = _lastPlayedTrack == null;
            string menuTitle = Localizer["menu.Title"];
            var title = lastPlayedTrackIsNull
                ? menuTitle
                : menuTitle + $"{NewLine}" + Localizer["menu.LastPlayedSong", _lastPlayedTrack!.Name];
            
            user!.Menu = new ChatMenu(title);

            if (user.Menu == null)
            {
                _logUtils.Log("user.Menu is nullable");
                return;
            }
            
            foreach (var (feature, state) in user.Settings)
            {
                var featureState = BoolStateToString(state);
                
                user.Menu.AddMenuOption(
                    Localizer[feature] + $" {featureState}",
                    (controller, _) =>
                    {
                        bool changedState = !state;
                        user.SetBoolProperty(feature, changedState);

                        string returnState = BoolStateToString(changedState);
                        
                        _players[steamId] = user;
                        _playersForSave[steamId] = user;

                        if (Config.ReOpenMenuAfterItemClick)
                        {
                            CreateMenu(controller);
                        }
                        else
                        {
                            player.PrintToChat($"{Localizer["chat.Prefix"]}{Localizer[feature]}: {returnState}");
                        }
                    });
            }
            
            if (!lastPlayedTrackIsNull)
                user.Menu.AddMenuOption(
                    Localizer["menu.ListenLastPlayedSong"],
                    (controller, _) =>
                    {
                        PlayLastSound(controller);
                    });
            
            MenuManager.OpenChatMenu(player, (ChatMenu)user.Menu);
        }
        
        private string BoolStateToString(bool state)
        {
            return state switch
            {
                true => $"{Localizer["chat.Enabled"]}",
                false => $"{Localizer["chat.Disabled"]}"
            };
        }
    }
}