
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Tetatt.GamePlay;
using Tetatt.Graphics;
using Microsoft.Xna.Framework.Net;
using Microsoft.Xna.Framework.GamerServices;
using Tetatt.Networking;

namespace Tetatt.Screens
{
    /// <summary>
    /// This screen implements the actual game logic.
    /// </summary>
    class GameplayScreen : GameScreen
    {
        public const int SendFieldStateDelay = 5;

        public const int blockSize = 32;

        public static Vector2[] Offsets = new Vector2[] {
            new Vector2(96, 248),
            new Vector2(384, 248),
            new Vector2(672, 248),
            new Vector2(960, 248),
        };

        // TODO accessor
        public static TileSet blocksTileSet;
        public static Texture2D background;
        public static Texture2D marker;
        public static SpriteFont font;
        Texture2D hasVoiceTexture;
        Texture2D isTalkingTexture;
        Texture2D voiceMutedTexture;

        SoundEffect[] popEffect;
        SoundEffect chainEffect;
        SoundEffect fanfare1Effect;
        SoundEffect fanfare2Effect;

        SoundEffect normalMusic;
        SoundEffect stressMusic;
        SoundEffectInstance music;
        int musicChangeTimer;
        bool isStressMusic;

        float pauseAlpha;

        /// <summary>
        /// The network session for this game.
        /// </summary>
        private NetworkSession networkSession;

        /// <summary>
        /// The packet writer used to send data from this screen.
        /// </summary>
        private PacketWriter packetWriter = new PacketWriter();

        /// <summary>
        /// The packet reader used to receive data to this screen..
        /// </summary>
        private PacketReader packetReader = new PacketReader();

        /// <summary>
        /// Constructor.
        /// </summary>
        public GameplayScreen(NetworkSession networkSession)
        {
            TransitionOnTime = TimeSpan.FromSeconds(1.5);
            TransitionOffTime = TimeSpan.FromSeconds(0.5);

            this.networkSession = networkSession;

            // set the networking events
            networkSession.GamerJoined += GamerJoined;
            networkSession.GamerLeft += GamerLeft;
            networkSession.GameStarted += GameStarted;
            networkSession.GameEnded += GameEnded;
        }

        /// <summary>
        /// Load graphics content for the game.
        /// </summary>
        public override void LoadContent()
        {
            ContentManager content = ScreenManager.Game.Content;

            // Load graphics
            background = content.Load<Texture2D>("playfield");
            marker = content.Load<Texture2D>("marker");
            blocksTileSet = new TileSet(
                content.Load<Texture2D>("blocks"), blockSize);
            font = content.Load<SpriteFont>("ingame_font");
            font.Spacing = -5;

            hasVoiceTexture = content.Load<Texture2D>("chat_able");
            isTalkingTexture = content.Load<Texture2D>("chat_talking");
            voiceMutedTexture = content.Load<Texture2D>("chat_mute");

            // Load sound effects
            popEffect = new SoundEffect[4];
            for (int i = 0; i < popEffect.Length; i++)
            {
                popEffect[i] = content.Load<SoundEffect>("pop" + (i + 1));
            }
            chainEffect = content.Load<SoundEffect>("chain");
            fanfare1Effect = content.Load<SoundEffect>("fanfare1");
            fanfare2Effect = content.Load<SoundEffect>("fanfare2");

            // Load music
            normalMusic = content.Load<SoundEffect>("normal_music");
            stressMusic = content.Load<SoundEffect>("stress_music");
        }

        /// <summary>
        /// Updates the state of the game. This method checks the GameScreen.IsActive
        /// property, so the game will stop updating when the pause menu is active,
        /// or if you tab away to a different application.
        /// </summary>
        public override void Update(GameTime gameTime, bool otherScreenHasFocus,
                                                       bool coveredByOtherScreen)
        {
            base.Update(gameTime, otherScreenHasFocus, false);

            if (IsExiting)
            {
                return;
            }

            ProcessPackets();

            if (networkSession.SessionState != NetworkSessionState.Playing)
            {
                // Show lobby screen if we're not playing and not already showing it
                if (!coveredByOtherScreen)
                {
                    ScreenManager.AddScreen(new LobbyScreen(this, networkSession), null);
                }

                // Check is game should start
                if (networkSession.IsHost &&
                    networkSession.IsEveryoneReady &&
                    networkSession.AllGamers.Count > 1)
                {
                    foreach (var gamer in networkSession.LocalGamers)
                    {
                        gamer.IsReady = false;
                    }
                    networkSession.StartGame();
                }
            }
            else
            {
                if (networkSession.IsHost)
                {
                    CheckEndOfGame();
                }

                // Send updates of play fields
                foreach (var gamer in networkSession.LocalGamers)
                {
                    Player data = (Player)gamer.Tag;
                    if (data.SendFieldStateTimer == 0)
                    {
                        SendFieldData(gamer);
                        data.SendFieldStateTimer = SendFieldStateDelay;
                    }
                    else
                    {
                        data.SendFieldStateTimer--;
                    }
                }

                // Switch to stressful music if anyone reaches a certain height, or back if
                // everyone is below again. Use a delay to avoid changing too often.
                bool anyStress = false;
                foreach (var gamer in networkSession.AllGamers)
                {
                    Player data = (Player)gamer.Tag;
                    if (data.PlayField.GetHeight() >= PlayField.stressHeight)
                    {
                        anyStress = true;
                        break;
                    }
                }
                if (anyStress != isStressMusic && --musicChangeTimer <= 0)
                {
                    music.Dispose();
                    music = (anyStress ? stressMusic : normalMusic).CreateInstance();
                    music.IsLooped = true;
                    music.Play();
                    isStressMusic = anyStress;
                    musicChangeTimer = 20;
                }
            }

            // Gradually fade in or out depending on whether we are covered by the pause screen.
            if (coveredByOtherScreen)
                pauseAlpha = Math.Min(pauseAlpha + 1f / 32, 1);
            else
                pauseAlpha = Math.Max(pauseAlpha - 1f / 32, 0);

            // Update playfields if not paused. Cannot pause in network mode.
            if (IsActive || networkSession.SessionType != NetworkSessionType.Local)
            {
                foreach (var gamer in networkSession.LocalGamers)
                {
                    Player data = (Player)gamer.Tag;
                    data.PlayField.Update();
                }
            }
        }


        /// <summary>
        /// Lets the game respond to player input. Unlike the Update method,
        /// this will only be called when the gameplay screen is active.
        /// </summary>
        public override void HandleInput(InputState input)
        {
            if (input == null)
                throw new ArgumentNullException("input");

            PlayerIndex playerIndex;

            // Check if anyone paused the game
            if (input.IsPauseGame(null, out playerIndex))
            {
                ShowPauseScreen(playerIndex);
                return;
            }

            // Check input for each player
            foreach (var gamer in networkSession.LocalGamers)
            {
                Player data = (Player)gamer.Tag;
                playerIndex = gamer.SignedInGamer.PlayerIndex;
                int playerInt = (int)playerIndex;

                KeyboardState keyboardState = input.CurrentKeyboardStates[playerInt];
                GamePadState gamePadState = input.CurrentGamePadStates[playerInt];

                // The game pauses if the user unplugs the active gamepad. This requires
                // us to keep track of whether a gamepad was ever plugged in, because we
                // don't want to pause on PC if they are playing with a keyboard and have
                // no gamepad at all!
                if (!gamePadState.IsConnected && input.GamePadWasConnected[playerInt])
                {
                    ShowPauseScreen(playerIndex);
                    return;
                }

                PlayerInput? playerInput = input.GetPlayerInput(playerIndex);
                if (playerInput.HasValue)
                {
                    data.PlayField.Input(playerInput.Value);
                }
            }
        }

        /// <summary>
        /// Exit this screen.
        /// </summary>
        public override void ExitScreen()
        {
            if (!IsExiting)
            {
                networkSession.GamerJoined -= GamerJoined;
                networkSession.GamerLeft -= GamerLeft;
                networkSession.GameStarted -= GameStarted;
                networkSession.GameEnded -= GameEnded;
            }
            if (music != null)
            {
                music.Dispose();
            }
            base.ExitScreen();
        }

        /// <summary>
        /// Screen-specific update to gamer rich presence.
        /// </summary>
        public override void UpdatePresence()
        {
            if (!IsExiting && (networkSession != null))
            {
                foreach (LocalNetworkGamer localGamer in networkSession.LocalGamers)
                {
                    SignedInGamer signedInGamer = localGamer.SignedInGamer;
                    if (signedInGamer.IsSignedInToLive)
                    {
                        if (networkSession.SessionType == NetworkSessionType.PlayerMatch)
                        {
                            signedInGamer.Presence.PresenceMode = GamerPresenceMode.OnlineVersus;
                        }
                        else
                        {
                            signedInGamer.Presence.PresenceMode = GamerPresenceMode.LocalVersus;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Draws the gameplay screen.
        /// </summary>
        public override void Draw(GameTime gameTime)
        {
            SpriteBatch spriteBatch = ScreenManager.SpriteBatch;

            foreach (var gamer in networkSession.AllGamers)
            {
                Player data = (Player)gamer.Tag;
                Vector2 offset = Offsets[networkSession.AllGamers.IndexOf(gamer)];

                spriteBatch.Begin();

                // Draw frame and background
                spriteBatch.Draw(
                    background,
                    offset - new Vector2(16, 16), // Adjust for the frame
                    Color.White * TransitionAlpha);

                // Draw gamertag and picture
                spriteBatch.DrawString(
                    font,
                    gamer.Gamertag,
                    new Vector2(0, -200) + offset,
                    Color.White * TransitionAlpha);
                if (data.GamerPicture != null)
                {
                    spriteBatch.Draw(
                        data.GamerPicture,
                        new Vector2(32, -170) + offset,
                        Color.White * TransitionAlpha);
                }

                // Draw the "is muted", "is talking", or "has voice" icon.
                Vector2 iconPosition = new Vector2(0, -170) + offset;
                if (gamer.IsMutedByLocalUser)
                {
                    spriteBatch.Draw(voiceMutedTexture, iconPosition,
                                     Color.White * TransitionAlpha);
                }
                else if (gamer.IsTalking)
                {
                    spriteBatch.Draw(isTalkingTexture, iconPosition,
                                     Color.White * TransitionAlpha);
                }
                else if (gamer.HasVoice)
                {
                    spriteBatch.Draw(hasVoiceTexture, iconPosition,
                                     Color.White * TransitionAlpha);
                }

                // Draw statistics
                string score = data.PlayField.Score.ToString();
                spriteBatch.DrawString(
                    font,
                    Resources.Score,
                    new Vector2(0, -75) + offset,
                    Color.White * TransitionAlpha);
                spriteBatch.DrawString(
                    font,
                    score,
                    new Vector2(200 - font.MeasureString(score).X, -75) + offset,
                    Color.White * TransitionAlpha);

                string time = String.Format("{0}:{1:00}", data.PlayField.Time / 60, data.PlayField.Time % 60);
                spriteBatch.DrawString(
                    font,
                    Resources.Time,
                    new Vector2(0, -45) + offset,
                    Color.White * TransitionAlpha);
                spriteBatch.DrawString(
                    font,
                    time,
                    new Vector2(200 - font.MeasureString(time).X, -45) + offset,
                    Color.White * TransitionAlpha);

                spriteBatch.End();

                // Setup sprite clipping using scissor test
                spriteBatch.Begin(SpriteSortMode.Deferred, null, null, null,
                    new RasterizerState()
                    {
                        ScissorTestEnable = true
                    });
                spriteBatch.GraphicsDevice.ScissorRectangle = new Rectangle(
                    (int)offset.X,
                    (int)offset.Y,
                    PlayField.width * blockSize,
                    PlayField.visibleHeight * blockSize);

                // Draw blocks
                data.PlayField.EachVisibleBlock((row, col, block) =>
                {
                    if (block != null)
                    {
                        int tile = block.Tile;
                        Vector2 pos = PosToVector(new Pos(row, col), data) + offset;
                        if(block.IsState(BlockState.Moving))
                            pos.X += (block.Right ? 1 : -1) * blockSize * block.StateDelay / 5;

                        spriteBatch.Draw(
                            blocksTileSet.Texture,
                            new Rectangle(
                                (int)pos.X,
                                (int)pos.Y,
                                blockSize,
                                blockSize),
                            blocksTileSet.SourceRectangle(tile),
                            ((row == 0 || data.PlayField.State == PlayFieldState.Dead) ? Color.DarkGray : Color.White) * TransitionAlpha);
                    }
                });

                spriteBatch.End();

                spriteBatch.Begin();
                if (data.PlayField.State == PlayFieldState.Play || data.PlayField.State == PlayFieldState.Start)
                {
                    // Draw marker
                    spriteBatch.Draw(
                        marker,
                        PosToVector(data.PlayField.markerPos, data) + offset - new Vector2(4, 5),
                        Color.White * TransitionAlpha);
                }

                if (data.PlayField.State == PlayFieldState.Start)
                {
                    string countdown = ((data.PlayField.StateDelay / 60) + 1).ToString();
                    Vector2 size = font.MeasureString(countdown);
                    spriteBatch.DrawString(
                        font,
                        countdown,
                        new Vector2(96, 96) - size / 2 + offset,
                        Color.White * TransitionAlpha);
                }

                spriteBatch.End();
            }

            base.Draw(gameTime);

            // If the game is covered by pause screen, fade it out to black.
            if (pauseAlpha > 0)
            {
                ScreenManager.FadeBackBufferToBlack(pauseAlpha / 2);
            }
        }

        public Vector2 PosToVector(Pos pos, Player data)
        {
            return new Vector2(
                pos.Col * blockSize,
                (PlayField.visibleHeight - pos.Row) * blockSize + (int)(data.PlayField.scrollOffset * blockSize));
        }

        /// <summary>
        /// Show message box asking if player wants to exit
        /// </summary>
        public void ShowPauseScreen(PlayerIndex playerIndex)
        {
            NetworkSessionComponent.LeaveSession(ScreenManager, playerIndex);
        }
        
        /// <summary>
        /// Called when a playfield performed a combo or a step in a chain 
        /// </summary>
        private void PerformedCombo(PlayField sender, Pos pos, bool isChain, int count)
        {
            LocalNetworkGamer gamer = GetPlayer(sender);
            Player data = (Player)gamer.Tag;
            Vector2 offset = Offsets[networkSession.AllGamers.IndexOf(gamer)];
            ScreenManager.Game.Components.Add(
                new EffCombo(ScreenManager, PosToVector(pos, data) + offset,
                    isChain, count,
                    sender.GetLevelData().effComboDuration));

            if (isChain)
            {
                chainEffect.Play();
            }
        }

        /// <summary>
        /// Called when a chain is completed
        /// </summary>
        private void PerformedChain(PlayField sender, Chain chain)
        {
            LocalNetworkGamer gamer = GetPlayer(sender);
            foreach (GarbageInfo info in chain.garbage)
            {
                SendGarbage(gamer, info.size, info.type);
            }
            if (chain.length > 1)
            {
                SendGarbage(gamer, chain.length - 1, GarbageType.Chain);
            }

            if (chain.length == 4)
            {
                fanfare1Effect.Play();
            }
            else if (chain.length > 4)
            {
                fanfare2Effect.Play();
            }
        }

        /// <summary>
        /// Called when a block is popped
        /// </summary>
        private void Popped(PlayField sender, Pos pos, bool isGarabge, Chain chain)
        {
            LocalNetworkGamer gamer = GetPlayer(sender);
            Player data = (Player)gamer.Tag;
            Vector2 offset = Offsets[networkSession.AllGamers.IndexOf(gamer)];

            ScreenManager.Game.Components.Add(
                new EffPop(ScreenManager, PosToVector(pos, data) + offset));

            SoundEffect effect = popEffect[Math.Min(chain.length, 4) - 1];
            effect.Play(1, chain.popCount / 10.0f, 0);

            if (chain.popCount < 10)
            {
                chain.popCount++;
            }
        }

        /// <summary>
        /// Called when a playfield dies
        /// </summary>
        private void Died(PlayField sender)
        {
            SendPlayerDied(GetPlayer(sender));
        }

        /// <summary>
        /// Returns a LocalNetworkGamer corresponding to a playfield, or null if not local.
        /// </summary>
        private LocalNetworkGamer GetPlayer(PlayField playField)
        {
            foreach (var gamer in networkSession.LocalGamers)
            {
                Player data = (Player)gamer.Tag;
                if (data.PlayField == playField)
                {
                    return gamer;
                }
            }
            return null;
        }

        /// <summary>
        /// Check end of game. Should only be called if we are the host.
        /// </summary>
        private void CheckEndOfGame()
        {
            NetworkGamer winner = null;
            int aliveCount = 0;
            foreach (var gamer in networkSession.AllGamers)
            {
                Player data = (Player)gamer.Tag;
                if (data.PlayField.State != PlayFieldState.Dead)
                {
                    winner = gamer;
                    aliveCount++;
                }
            }

            if (aliveCount <= 1)
            {
                networkSession.EndGame();

                if (aliveCount == 1)
                {
                    Player data = (Player)winner.Tag;
                    data.Wins++;
                    SendPlayerData(winner);
                    data.PlayField.State = PlayFieldState.Dead;
                }
            }
        }

        /// <summary>
        /// Called when network game is started.
        /// </summary>
        private void GameStarted(object sender, GameStartedEventArgs e)
        {
            // Remove lobby screen
            foreach (var screen in ScreenManager.GetScreens())
            {
                if (screen is LobbyScreen)
                    screen.ExitScreen();
            }

            foreach (var gamer in networkSession.AllGamers)
            {
                Player data = (Player)gamer.Tag;
                data.PlayField.Reset();
                data.PlayField.Level = data.StartLevel;
                data.PlayField.Start();

                if (!gamer.IsLocal)
                {
                    data.PlayField.State = PlayFieldState.Play;
                }
            }

            music = normalMusic.CreateInstance();
            music.IsLooped = true;
            music.Play();
        }

        /// <summary>
        /// Called when network game ended.
        /// </summary>
        private void GameEnded(object sender, GameEndedEventArgs e)
        {
            foreach (var gamer in networkSession.LocalGamers)
            {
                Player data = (Player)gamer.Tag;
                data.PlayField.Stop();

                if (!gamer.IsLocal)
                {
                    gamer.IsReady = true;
                }
            }

            if (music != null)
            {
                music.Dispose();
            }
        }

        /// <summary>
        /// Called when gamer joined network game.
        /// </summary>
        private void GamerJoined(object sender, GamerJoinedEventArgs e)
        {
            Player data = new Player();
            data.PlayField = new PlayField(Player.DefaultLevel);
            data.PlayField.PerformedCombo += PerformedCombo;
            data.PlayField.PerformedChain += PerformedChain;
            data.PlayField.Popped += Popped;
            data.PlayField.Died += Died;

            e.Gamer.Tag = data;

            e.Gamer.BeginGetProfile(GetProfile, e.Gamer);
        }

        /// <summary>
        /// Called when getting a profile asychronously is finished.
        /// </summary>
        private void GetProfile(IAsyncResult r)
        {
            try
            {
                NetworkGamer gamer = (NetworkGamer)r.AsyncState;
                GamerProfile profile = gamer.EndGetProfile(r);
                ((Player)gamer.Tag).GamerPicture = Texture2D.FromStream(
                    ScreenManager.GraphicsDevice, profile.GetGamerPicture());
            }
            catch (GamerPrivilegeException)
            {
                // Not a Live profile. Can happen if playing against a local profile
                // over System Link.
            }
            catch (InvalidOperationException)
            {
                // Not sure what the difference is to GamerPrivilegeException... Seems
                // to happen when a local gamer is signed in with a local profile.
            }
        }

        /// <summary>
        /// Called when network player left.
        /// </summary>
        private void GamerLeft(object sender, GamerLeftEventArgs e)
        {
            Player data = (Player)e.Gamer.Tag;
            data.PlayField.PerformedCombo -= PerformedCombo;
            data.PlayField.PerformedChain -= PerformedChain;
            data.PlayField.Popped -= Popped;
            data.PlayField.Died -= Died;
        }

        /// <summary>
        /// Process incoming packets on the local gamer.
        /// </summary>
        private void ProcessPackets()
        {
            foreach (var receiver in networkSession.LocalGamers)
            {
                Player receiverData = (Player)receiver.Tag;
                while (receiver.IsDataAvailable)
                {
                    NetworkGamer sender;
                    receiver.ReceiveData(packetReader, out sender);
                    Player senderData = (Player)sender.Tag;

                    PacketTypes packetType = (PacketTypes)packetReader.ReadByte();
                    switch (packetType)
                    {
                        case PacketTypes.PlayerData:
                            NetworkGamer gamer = networkSession.FindGamerById(packetReader.ReadByte());
                            Player data = (Player)gamer.Tag;
                            data.Wins = packetReader.ReadByte();
                            data.StartLevel = packetReader.ReadByte();
                            break;

                        case PacketTypes.PlayerDied:
                            senderData.PlayField.State = PlayFieldState.Dead;
                            break;

                        case PacketTypes.FieldData:
                            if (!sender.IsLocal)
                            {
                                senderData.PlayField.scrollOffset = ((int)packetReader.ReadByte() - blockSize) / (double)blockSize;
                                for (int i = 2; i < packetReader.Length; i += 3)
                                {
                                    int row = packetReader.ReadByte();
                                    int col = packetReader.ReadByte();
                                    int tile = packetReader.ReadByte();
                                    Block block = (tile != 255) ? new Block(
                                        BlockType.Blue, BlockState.Idle, null, true, new Anim(tile)) : null;
                                    senderData.PlayField.field[row, col] = block;
                                }
                            }
                            break;

                        case PacketTypes.Garbage:
                            int size = packetReader.ReadByte();
                            GarbageType type = (GarbageType)packetReader.ReadByte();
                            receiverData.PlayField.AddGarbage(size, type);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Send player data to all players
        /// </summary>
        /// <param name="player">Player to send</param>
        public void SendPlayerData(NetworkGamer gamer)
        {
            Player data = (Player)gamer.Tag;
            packetWriter.Write((byte)PacketTypes.PlayerData);
            packetWriter.Write((byte)gamer.Id);
            packetWriter.Write((byte)data.Wins);
            packetWriter.Write((byte)data.StartLevel);
            networkSession.LocalGamers[0].SendData(
                packetWriter,
                SendDataOptions.ReliableInOrder);
        }

        /// <summary>
        /// Sends that a player died to all players.
        /// </summary>
        /// <param name="player">Player to send</param>
        public void SendPlayerDied(LocalNetworkGamer gamer)
        {
            if (networkSession != null)
            {
                packetWriter.Write((byte)PacketTypes.PlayerDied);
                gamer.SendData(packetWriter, SendDataOptions.ReliableInOrder);
            }
        }

        /// <summary>
        /// Determine target and send garbage to this player, possibly
        /// over the network.
        /// </summary>
        public void SendGarbage(LocalNetworkGamer sender, int size, GarbageType type)
        {
            // Determine receiver
            NetworkGamer receiver = sender;
            int index = networkSession.AllGamers.IndexOf(sender);
            for (int i = 1; i < networkSession.AllGamers.Count; i++)
            {
                var gamer = networkSession.AllGamers[(index + i) % networkSession.AllGamers.Count];
                Player data = (Player)gamer.Tag;
                if (data.PlayField.State == PlayFieldState.Play)
                    receiver = gamer;
            }

            // Send packet
            packetWriter.Write((byte)PacketTypes.Garbage);
            packetWriter.Write((byte)size);
            packetWriter.Write((byte)type);
            sender.SendData(packetWriter, SendDataOptions.ReliableInOrder, receiver);
        }

        /// <summary>
        /// Send delta of field state.
        /// </summary>
        public void SendFieldData(LocalNetworkGamer gamer)
        {
            Player data = (Player)gamer.Tag;
            packetWriter.Write((byte)PacketTypes.FieldData);
            packetWriter.Write((byte)(data.PlayField.scrollOffset * blockSize + blockSize));

            data.PlayField.EachVisibleBlock((row, col, block) =>
                {
                    int tile = (block != null) ? block.Tile : 255;
                    if (tile != data.LastFieldState[row, col])
                    {
                        packetWriter.Write((byte)row);
                        packetWriter.Write((byte)col);
                        packetWriter.Write((byte)tile);
                        data.LastFieldState[row, col] = tile;
                    }
                }
            );
            gamer.SendData(packetWriter, SendDataOptions.InOrder);
        }
    }
}
