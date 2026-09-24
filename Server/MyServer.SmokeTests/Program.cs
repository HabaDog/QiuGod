using System.Collections.Concurrent;
using GameNetworkCommon;
using ProtoBuf;

var broadcasts = new ConcurrentQueue<(int[] PlayerIds, ushort ProtocolId)>();
using var sessions = new GameSessionService();
sessions.BroadcastToPlayers += (playerIds, packet) =>
{
    var message = BaseMessage.FromBytes(packet, 0, packet.Length);
    broadcasts.Enqueue((playerIds.ToArray(), message.ProtocolId));
};

Assert(!sessions.StartSession(Array.Empty<int>()).Success, "Empty sessions must be rejected.");
Assert(!sessions.StartSession(new[] { 101, 101 }).Success, "Duplicate players must be rejected.");

VerifyProtocolContract();
VerifySharedProtoCoverage();
VerifyTcpFrameDecoder();
VerifyHotUpdateReleaseTransactions();
VerifyGameModeExtensionBoundary();
VerifyGameThreeBallPresentationCatalog();
VerifyGameThreePowerUpCatalog();
VerifyGameThreeBallSelectionRecovery();
VerifyMatchmakingQueuePolicy();
VerifyPostMatchReturnBarrier();
VerifyGameThreeFinalSurvivorSnapshot();
VerifyGameThreeKinematicMotion();
VerifyGameThreeInputAcknowledgement();
await VerifyGameThreeSession();
await VerifyGameThreeLlmAiLifecycle();
await VerifyGameThreeUtilityAi();
VerifyGameThreeLightweightPhysics();
VerifyClientGameModuleRegistry();

sessions.StopAll();

Console.WriteLine("GameSessionService smoke tests passed.");

static void VerifyProtocolContract()
{
    var protocolIds = typeof(Protocols)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.FieldType == typeof(ushort))
        .Select(field => (ushort)field.GetRawConstantValue()!)
        .ToArray();
    Assert(protocolIds.Length == protocolIds.Distinct().Count(), "Protocol IDs must be unique.");

    var sourcePacket = new BaseMessage(Protocols.HeartbeatRequest, new byte[] { 1, 2, 3 });
    var decodedPacket = BaseMessage.FromBytes(sourcePacket.ToBytes(), 0, sourcePacket.TotalLength);
    Assert(
        decodedPacket.ProtocolId == Protocols.HeartbeatRequest
        && decodedPacket.Body.SequenceEqual(new byte[] { 1, 2, 3 }),
        "BaseMessage must round-trip protocol ID and payload.");

    var deadPlayerBody = MessageCodec.EncodeBody(new GameThreePlayerState
    {
        PlayerId = 7,
        SlotIndex = 2,
        CurrentHealth = 0,
        MaxHealth = 100,
        IsAlive = false
    });
    var decodedDeadPlayer = MessageCodec.Decode<GameThreePlayerState>(
        new BaseMessage(Protocols.GameThreeStateSync, deadPlayerBody));
    Assert(
        !decodedDeadPlayer.IsAlive && decodedDeadPlayer.CurrentHealth == 0,
        "A dead GamePlayThree player must remain dead after protobuf round-trip.");

    var loginBody = MessageCodec.EncodeBody(new LoginRequest
    {
        Username = "alice",
        Password = "secret"
    });
    var expectedLoginBody = new byte[]
    {
        0x0A, 0x05, 0x61, 0x6C, 0x69, 0x63, 0x65,
        0x12, 0x06, 0x73, 0x65, 0x63, 0x72, 0x65, 0x74
    };
    Assert(
        loginBody.SequenceEqual(expectedLoginBody),
        "LoginRequest protobuf bytes changed; field numbers are a wire compatibility contract.");

    var loginPacket = MessageCodec.EncodePacket(
        Protocols.LoginRequest,
        new LoginRequest { Username = "alice", Password = "secret" });
    var expectedPacketPrefix = new byte[] { 0xD1, 0x07, 0x0F, 0x00, 0x00, 0x00 };
    Assert(
        loginPacket.Take(BaseMessage.HeaderSize).SequenceEqual(expectedPacketPrefix)
        && loginPacket.Skip(BaseMessage.HeaderSize).SequenceEqual(expectedLoginBody),
        "The TCP packet envelope or protobuf body changed unexpectedly.");
}

static void VerifyGameThreePowerUpCatalog()
{
    var expectedTypeIds = new[] { 1, 2, 3, 4, 5, 6 };
    Assert(
        GameThreePowerUpCatalogData.All.Select(item => item.TypeId).OrderBy(typeId => typeId).SequenceEqual(expectedTypeIds),
        "GameThree power-up catalog must preserve its protocol type IDs.");
    Assert(
        GameThreePowerUpCatalogData.All.All(item => item.VisualScaleX > 0f
            && item.VisualScaleY > 0f
            && item.VisualScaleZ > 0f
            && item.SpawnWeight >= 0
            && !string.IsNullOrWhiteSpace(item.DisplayName)
            && !string.IsNullOrWhiteSpace(item.ShortLabel)),
        "GameThree power-up catalog entries must be valid for gameplay and presentation.");
    Assert(
        GameThreePowerUpCatalogData.TryGet(5, out var shield)
        && shield.DurationSeconds == 4f
        && shield.ShortLabel == "SHD",
        "GameThree shield settings must be available from the shared catalog.");
}

static void VerifyGameThreeBallPresentationCatalog()
{
    Assert(
        GameThreeBallCatalogData.All.All(item => !string.IsNullOrWhiteSpace(item.SkillDisplayName)
            && item.SkillEffectRed is >= 0f and <= 1f
            && item.SkillEffectGreen is >= 0f and <= 1f
            && item.SkillEffectBlue is >= 0f and <= 1f
            && item.SkillBurstSeconds > 0f
            && item.SkillAnnouncementSeconds > 0f),
        "GameThree ball presentation settings must be valid for every skill.");
    Assert(
        GameThreeBallCatalogData.TryGetBySkillId("time_stop", out var timeStop)
        && timeStop.SkillDisplayName == "TIME STOP"
        && timeStop.SkillAnnouncementSeconds == 1.9f,
        "GameThree skill presentation must be queryable by its network skill ID.");
}

static void VerifyMatchmakingQueuePolicy()
{
    var fullQueue = new LobbyDirectoryService();
    var fullMatches = new List<(IReadOnlyList<int> Players, string RoomId)>();
    fullQueue.MatchFound += (players, roomId) => fullMatches.Add((players, roomId));
    for (var index = 0; index < LobbyDirectoryService.MaxRoomPlayers; index++)
    {
        var result = fullQueue.JoinMatchmaking(1001 + index, $"queue-{index}", GameModes.GameThree);
        Assert(result.Success, "Every eligible player should enter matchmaking.");
    }
    Assert(
        fullMatches.Count == 1 && fullMatches[0].Players.Count == LobbyDirectoryService.MaxRoomPlayers,
        "Four queued players must be matched immediately into one room.");

    var timeoutQueue = new LobbyDirectoryService();
    var timeoutMatches = new List<(IReadOnlyList<int> Players, string RoomId)>();
    timeoutQueue.MatchFound += (players, roomId) => timeoutMatches.Add((players, roomId));
    var queuedAt = DateTime.UtcNow;
    var waiting = timeoutQueue.JoinMatchmaking(2001, "timeout-player", GameModes.GameThree);
    Assert(waiting.Success && !waiting.MatchFound, "A partial queue must wait for more players.");
    timeoutQueue.ProcessMatchmakingTimeouts(queuedAt.AddSeconds(29));
    Assert(timeoutMatches.Count == 0, "A partial queue must not start before 30 seconds.");
    timeoutQueue.ProcessMatchmakingTimeouts(queuedAt.AddSeconds(31));
    Assert(
        timeoutMatches.Count == 1 && timeoutMatches[0].Players.SequenceEqual(new[] { 2001 }),
        "A partial queue must start after 30 seconds so the game session can fill empty slots with AI.");
}

static void VerifyGameThreeBallSelectionRecovery()
{
    var server = new GameServer(new ProbeAccountService());
    try
    {
        var serverType = typeof(GameServer);
        var prepareMethod = serverType.GetMethod(
            "PrepareBallSelection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var pendingByPlayerField = serverType.GetField(
            "_pendingBallSelectionByPlayer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (prepareMethod is null || pendingByPlayerField is null)
        {
            throw new InvalidOperationException("GameThree ball-selection recovery internals must remain available to the smoke test.");
        }

        object Prepare(params int[] playerIds)
        {
            return prepareMethod.Invoke(server, new object[] { playerIds, GameModes.GameThree, "Room" })
                ?? throw new InvalidOperationException("Ball-selection preparation returned no result.");
        }

        static string GetDisposition(object preparation)
        {
            return preparation.GetType().GetProperty("Disposition")?.GetValue(preparation)?.ToString()
                ?? string.Empty;
        }

        var first = Prepare(9101, 9102);
        Assert(GetDisposition(first) == "Created", "The first request must create a ball-selection session.");

        var pendingByPlayer = (System.Collections.IDictionary)pendingByPlayerField.GetValue(server)!;
        var pending = pendingByPlayer[9101]
            ?? throw new InvalidOperationException("The created ball-selection session must index every participant.");
        var confirmedByPlayer = (System.Collections.IDictionary)(pending.GetType()
            .GetProperty("BallSelections")?.GetValue(pending)
            ?? throw new InvalidOperationException("The pending selection must expose confirmed balls."));
        confirmedByPlayer[9101] = "sprinter";

        var resumed = Prepare(9101, 9102);
        var resumedSelections = (System.Collections.IEnumerable)(resumed.GetType()
            .GetProperty("Selections")?.GetValue(resumed)
            ?? throw new InvalidOperationException("A resumed selection must expose its current confirmations."));
        Assert(
            GetDisposition(resumed) == "Resumed" && resumedSelections.Cast<object>().Count() == 1,
            "The same room must resume its pending selection and preserve confirmed balls.");

        var replaced = Prepare(9101, 9103);
        var indexedPlayerIds = pendingByPlayer.Keys.Cast<int>().OrderBy(playerId => playerId).ToArray();
        Assert(
            GetDisposition(replaced) == "Replaced"
            && indexedPlayerIds.SequenceEqual(new[] { 9101, 9103 }),
            "A changed room must replace stale selection indexes for every old participant.");
    }
    finally
    {
        server.Stop();
    }
}

static void VerifyPostMatchReturnBarrier()
{
    var server = new GameServer(new ProbeAccountService());
    try
    {
        var serverType = typeof(GameServer);
        var completedMethod = serverType.GetMethod(
            "HandleGameSessionCompleted",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var returnedMethod = serverType.GetMethod(
            "HandlePlayerReturnedToRoom",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var canStartMethod = serverType.GetMethod(
            "CanStartAfterMatch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (completedMethod is null || returnedMethod is null || canStartMethod is null)
        {
            throw new InvalidOperationException("Post-match return barrier internals must remain available to the smoke test.");
        }

        bool CanStart(params int[] playerIds)
        {
            var args = new object[] { playerIds, string.Empty };
            return (bool)(canStartMethod.Invoke(server, args) ?? false);
        }

        completedMethod.Invoke(server, new object[] { new[] { 9201, 9202 }, GameModes.GameThree });
        Assert(!CanStart(9201, 9202), "A completed match must remain blocked until every player returns.");

        returnedMethod.Invoke(server, new object[] { 9201 });
        Assert(!CanStart(9201, 9202), "One returned player must not release the room barrier.");

        returnedMethod.Invoke(server, new object[] { 9202 });
        Assert(CanStart(9201, 9202), "The room must become startable after every player returns.");
    }
    finally
    {
        server.Stop();
    }
}

static void VerifyGameThreeFinalSurvivorSnapshot()
{
    var packetTypes = new List<ushort>();
    var finalSnapshots = new List<GameThreeStateSync>();
    var game = new GameThreeSession(startDelay: TimeSpan.FromSeconds(30), randomSeed: 91);
    game.Broadcast += packet =>
    {
        var message = BaseMessage.FromBytes(packet, 0, packet.Length);
        packetTypes.Add(message.ProtocolId);
        if (message.ProtocolId == Protocols.GameThreeStateSync)
        {
            finalSnapshots.Add(MessageCodec.Decode<GameThreeStateSync>(message));
        }
    };

    game.Start(new[] { 901 });
    var playersField = typeof(GameThreeSession).GetField("_players", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var updateMatchMethod = typeof(GameThreeSession).GetMethod("UpdateMatch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var roundActiveField = typeof(GameThreeSession).GetField("_isRoundActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (playersField == null || updateMatchMethod == null || roundActiveField == null)
    {
        throw new InvalidOperationException("GameThree final-state internals must remain available to the smoke test.");
    }

    var players = (Dictionary<int, GameThreeBallState>)playersField.GetValue(game)!;
    foreach (var player in players.Values.Where(player => player.SlotIndex != 0))
    {
        player.IsAlive = false;
        player.CurrentHealth = 0;
    }

    roundActiveField.SetValue(game, true);
    updateMatchMethod.Invoke(game, new object?[] { null });

    var gameOverIndex = packetTypes.LastIndexOf(Protocols.GameThreeGameOver);
    Assert(
        gameOverIndex > 0 && packetTypes[gameOverIndex - 1] == Protocols.GameThreeStateSync,
        "GameThree must send a final state snapshot immediately before GameOver.");
    Assert(
        finalSnapshots.Count > 0
        && !finalSnapshots[^1].IsRoundActive
        && finalSnapshots[^1].Players.Count(player => player.IsAlive) == 1,
        "GameThree final snapshot must leave exactly one survivor for result presentation.");
    game.Stop();
}

static void VerifySharedProtoCoverage()
{
    var protoPath = FindFromCurrentDirectory("Shared/Protocol/game_messages.proto");
    var proto = File.ReadAllText(protoPath);

    var protocolEntries = typeof(Protocols)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.FieldType == typeof(ushort))
        .Select(field => (field.Name, Value: (ushort)field.GetRawConstantValue()!))
        .ToArray();

    foreach (var entry in protocolEntries)
    {
        Assert(
            proto.Contains($"{entry.Name} = {entry.Value};", StringComparison.Ordinal),
            $"Shared proto must define protocol {entry.Name} = {entry.Value}.");
    }

    var messageTypes = typeof(LoginRequest).Assembly
        .GetTypes()
        .Where(type => type.Namespace == "GameNetworkCommon")
        .Where(type => type.GetCustomAttributes(typeof(ProtoContractAttribute), inherit: false).Any())
        .Select(type => type.Name)
        .OrderBy(name => name)
        .ToArray();

    foreach (var messageType in messageTypes)
    {
        Assert(
            proto.Contains($"message {messageType} ", StringComparison.Ordinal),
            $"Shared proto must define message {messageType}.");
    }
}

static void VerifyTcpFrameDecoder()
{
    var first = new BaseMessage(Protocols.HeartbeatRequest, new byte[] { 1, 2, 3 }).ToBytes();
    var second = new BaseMessage(Protocols.LogoutRequest, new byte[] { 4, 5 }).ToBytes();
    var combined = first.Concat(second).ToArray();
    var decoder = new MessageFrameDecoder(64 * 1024);

    decoder.Append(combined, 0, 4);
    Assert(!decoder.TryRead(out _), "A partial header must not produce a frame.");
    decoder.Append(combined, 4, first.Length - 4);
    Assert(
        decoder.TryRead(out var firstDecoded)
        && firstDecoded.ProtocolId == Protocols.HeartbeatRequest
        && firstDecoded.Body.SequenceEqual(new byte[] { 1, 2, 3 }),
        "A fragmented TCP frame must be reassembled.");

    decoder.Append(combined, first.Length, second.Length);
    Assert(
        decoder.TryRead(out var secondDecoded)
        && secondDecoded.ProtocolId == Protocols.LogoutRequest
        && secondDecoded.Body.SequenceEqual(new byte[] { 4, 5 }),
        "Back-to-back TCP frames must be decoded independently.");
    Assert(!decoder.TryRead(out _), "The decoder must consume complete frames exactly once.");

    var invalidHeader = new byte[BaseMessage.HeaderSize];
    BitConverter.GetBytes(64 * 1024 + 1).CopyTo(invalidHeader, sizeof(ushort));
    decoder.Append(invalidHeader, 0, invalidHeader.Length);
    try
    {
        decoder.TryRead(out _);
        throw new InvalidOperationException("An oversized frame must be rejected.");
    }
    catch (InvalidDataException)
    {
    }
}

static void VerifyHotUpdateReleaseTransactions()
{
    var root = Path.Combine(Path.GetTempPath(), "mydemo-hot-update-smoke-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new HotUpdateReleaseStore(root);
        var firstBytes = new byte[] { 1, 2, 3 };
        var first = CreateRelease("release-1", "content.bundle", firstBytes);
        store.PrepareStaging(first);
        File.WriteAllBytes(store.GetStagingFilePath(first.releaseId, first.files[0].path), firstBytes);
        Assert(store.CommitStaging(first, "{}", out var firstError), $"First release commit failed: {firstError}");
        Assert(store.ReadActiveReleaseId() == first.releaseId, "The first committed release must become active.");

        var secondBytes = new byte[] { 4, 5, 6, 7 };
        var second = CreateRelease("release-2", "content.bundle", secondBytes);
        store.PrepareStaging(second);
        File.WriteAllBytes(store.GetStagingFilePath(second.releaseId, second.files[0].path), new byte[] { 9 });
        Assert(!store.CommitStaging(second, "{}", out _), "A corrupt staged release must not commit.");
        Assert(store.ReadActiveReleaseId() == first.releaseId, "A failed commit must preserve the active release.");

        store.PrepareStaging(second);
        File.WriteAllBytes(store.GetStagingFilePath(second.releaseId, second.files[0].path), secondBytes);
        Assert(store.CommitStaging(second, "{}", out var secondError), $"Second release commit failed: {secondError}");
        Assert(store.ReadActiveReleaseId() == second.releaseId, "The second valid release must become active.");
        Assert(store.ReadPreviousReleaseId() == first.releaseId, "The previous release pointer must be preserved.");

        File.WriteAllBytes(store.GetReleaseFilePath(second.releaseId, second.files[0].path), new byte[] { 0 });
        Assert(!store.ValidateRelease(second, out _), "A corrupted active release must fail validation.");
        Assert(store.TryRollback(out var rolledBack) && rolledBack == first.releaseId, "Rollback must restore the previous release.");
        Assert(store.ReadActiveReleaseId() == first.releaseId, "Rollback must update the active pointer.");

        var abandoned = CreateRelease("release-4", "content.bundle", new byte[] { 8, 8, 8 });
        store.PrepareStaging(abandoned);
        var expiredReleasePath = Path.Combine(root, "releases", second.releaseId);
        Directory.SetLastWriteTimeUtc(expiredReleasePath, DateTime.UtcNow.AddDays(-2));
        var cleanup = store.PruneCache(maxSizeMegabytes: 500, expireDays: 1);
        Assert(!Directory.Exists(expiredReleasePath), "Expired non-rollback releases must be removed from cache.");
        Assert(
            !Directory.Exists(Path.Combine(root, "staging", abandoned.releaseId)),
            "Interrupted staging downloads must be removed from cache.");
        Assert(
            Directory.Exists(Path.Combine(root, "releases", first.releaseId))
            && cleanup.RemovedReleaseCount == 1
            && cleanup.RemovedStagingCount == 1,
            "Cache cleanup must retain the active rollback target and report removed entries.");

        var invalid = CreateRelease("release-3", "../outside.bundle", new byte[] { 1 });
        try
        {
            HotUpdateReleaseStore.ValidateManifestMetadata(invalid);
            throw new InvalidOperationException("Traversal paths in a release manifest must be rejected.");
        }
        catch (ArgumentException)
        {
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static HotUpdateReleaseManifest CreateRelease(string releaseId, string path, byte[] content)
{
    return new HotUpdateReleaseManifest
    {
        releaseId = releaseId,
        generatedAtUtc = DateTime.UtcNow.ToString("O"),
        files = new List<HotUpdateReleaseFile>
        {
            new()
            {
                path = path,
                size = content.LongLength,
                sha256 = HotUpdateReleaseStore.ComputeSha256(content)
            }
        }
    };
}

static void VerifyGameModeExtensionBoundary()
{
    using var sessions = new GameSessionService(new IGameSessionFactory[]
    {
        new ProbeGameSessionFactory()
    });

    var unknown = sessions.StartSession(new[] { 201 }, "missing_mode");
    Assert(!unknown.Success, "An unregistered game mode must be rejected.");

    var started = sessions.StartSession(new[] { 201 }, ProbeGameSessionFactory.Mode);
    Assert(started.Success, "A registered game mode factory must start a session.");
    Assert(
        started.GameMode == ProbeGameSessionFactory.Mode
        && started.SceneName == ProbeGameSessionFactory.Scene,
        "A game session must expose its mode and client scene.");
    Assert(
        sessions.HandleMessage(201, new BaseMessage(ProbeGameSession.CommandProtocol, Array.Empty<byte>())),
        "Game messages must route to the active mode implementation.");
    Assert(
        sessions.TryGetResumeState(201, out var resume)
        && resume is not null
        && resume.SnapshotPacket.ProtocolId == ProbeGameSession.SnapshotProtocol,
        "A game mode must provide its own recovery packet.");
}

static async Task VerifyGameThreeSession()
{
    Assert(
        GameThreeSession.DefaultMapWidth == 50
        && GameThreeSession.DefaultMapHeight == 50
        && Math.Abs(GameThreeSession.DefaultRoundDurationSeconds - 180f) < 0.001f,
        "GamePlayThree release defaults must remain 50x50 with a three-minute round.");

    var protocolIds = new ConcurrentQueue<ushort>();
    var game = new GameThreeSession(
        startDelay: TimeSpan.Zero,
        tickInterval: TimeSpan.FromMilliseconds(5),
        roundDurationSeconds: 5f,
        bombFuseSeconds: 0.03f,
        randomSeed: 7);
    game.Broadcast += packet =>
    {
        var message = BaseMessage.FromBytes(packet, 0, packet.Length);
        protocolIds.Enqueue(message.ProtocolId);
    };

    try
    {
        game.Start(new[] { 301 });
        var initialSnapshot = MessageCodec.Decode<GameThreeStateSync>(game.CreateRecoverySnapshot(301));
        Assert(
            game.GameMode == GameModes.GameThree
            && game.SceneName == "GamePlayThree"
            && initialSnapshot.IsRecoverySnapshot
            && initialSnapshot.IsRoundActive
            && initialSnapshot.MapWidth == GameThreeSession.DefaultMapWidth
            && initialSnapshot.MapHeight == GameThreeSession.DefaultMapHeight
            && initialSnapshot.ServerTimestampMs > 0
            && initialSnapshot.SnapshotSequence > 0
            && initialSnapshot.TerritoryGrid.Count == GameThreeSession.DefaultMapWidth * GameThreeSession.DefaultMapHeight
            && initialSnapshot.TerritoryRevisionGrid.Count == GameThreeSession.DefaultMapWidth * GameThreeSession.DefaultMapHeight,
            "GamePlayThree recovery state must contain the authoritative map, server clock, snapshot sequence, and tile revisions.");
        Assert(
            initialSnapshot.Players.Count == GameThreeSession.MaxMatchPlayers
            && initialSnapshot.Players.Count(player => player.IsAi) == 3
            && initialSnapshot.Players.Select(player => player.BallId).Distinct(StringComparer.Ordinal).Count() == initialSnapshot.Players.Count
            && initialSnapshot.Players.Single(player => player.PlayerId == 301).CurrentHealth == GameThreeSession.DefaultMaxHealth
            && initialSnapshot.Players.Single(player => player.PlayerId == 301).EffectiveMoveSpeed > 0f,
            "GamePlayThree must fill empty slots with unique-ball AI and initialize ball health and movement speed.");

        Assert(
            !game.HandleMessage(
                301,
                new BaseMessage(
                    Protocols.GameThreeInput,
                    MessageCodec.EncodeBody(new GameThreeInput
                    {
                        PlayerId = 302,
                        MoveX = 1f,
                        Sequence = 1,
                        DeltaTime = 0.05f
                    }))),
            "GamePlayThree must reject a command whose packet player ID does not match the authenticated player.");
        Assert(
            !game.HandleMessage(
                301,
                new BaseMessage(
                    Protocols.GameThreeInput,
                    MessageCodec.EncodeBody(new GameThreeInput
                    {
                        PlayerId = 301,
                        MoveX = 1f,
                        Sequence = 1,
                        DeltaTime = 0f
                    }))),
            "GamePlayThree must reject movement commands without a simulation delta time.");
        Assert(
            game.HandleMessage(
                301,
                new BaseMessage(
                    Protocols.GameThreeInput,
                    MessageCodec.EncodeBody(new GameThreeInput
                    {
                        PlayerId = 301,
                        MoveX = 1f,
                        Sequence = 1,
                        DeltaTime = 0.05f
                    }))),
            "A valid GamePlayThree movement input must be accepted.");
        Assert(
            game.HandleMessage(
                301,
                new BaseMessage(
                    Protocols.GameThreeInput,
                    MessageCodec.EncodeBody(new GameThreeInput
                    {
                        PlayerId = 301,
                        MoveX = 1f,
                        Jump = true,
                        Sequence = 2,
                        DeltaTime = 0.05f
                    }))),
            "A valid GamePlayThree jump input must be accepted.");
        await Task.Delay(40);
        var afterMovement = MessageCodec.Decode<GameThreeStateSync>(game.CreateRecoverySnapshot(301))
            .Players.Single(player => player.PlayerId == 301);
        Assert(
            afterMovement.PositionY > 0.5f && afterMovement.VelocityX > 0f,
            "GamePlayThree jump and force-based horizontal velocity must be server-authoritative and included in a recovery snapshot.");
        Assert(
            !game.HandleMessage(
                301,
                new BaseMessage(
                    Protocols.GameThreeUseSkillRequest,
                    MessageCodec.EncodeBody(new GameThreeUseSkillRequest
                    {
                        PlayerId = 301,
                        DirectionX = -1f,
                        Sequence = 1
                    }))),
            "A ball must not use its energy skill before claiming enough tiles.");
        Assert(
            game.HandleMessage(
                301,
                new BaseMessage(
                    Protocols.GameThreeDashRequest,
                    MessageCodec.EncodeBody(new GameThreeDashRequest
                    {
                        PlayerId = 301,
                        DirectionX = -1f,
                        Sequence = 1
                    }))),
            "A living player must be able to use its universal dash.");
        Assert(
            game.HandleMessage(
                301,
                new BaseMessage(
                    Protocols.GameThreeThrowBombRequest,
                    MessageCodec.EncodeBody(new GameThreeThrowBombRequest
                    {
                        PlayerId = 301,
                        DirectionX = -1f,
                        Sequence = 1
                    }))),
            "A living player must be able to throw a bomb.");

        var afterActions = MessageCodec.Decode<GameThreeStateSync>(game.CreateRecoverySnapshot(301));
        var localPlayer = afterActions.Players.Single(player => player.PlayerId == 301);
        Assert(
            afterActions.Bombs.Count == 1 && localPlayer.DashCooldownRemaining > 0f,
            "The recovery snapshot must include active bombs and the server-authoritative dash cooldown.");

        await Task.Delay(150);
        var afterExplosion = MessageCodec.Decode<GameThreeStateSync>(game.CreateRecoverySnapshot(301));
        Assert(
            protocolIds.Contains(Protocols.GameThreeRoundStart)
            && protocolIds.Contains(Protocols.GameThreeBombThrown)
            && protocolIds.Contains(Protocols.GameThreeBombExploded),
            "GamePlayThree must broadcast round and bomb lifecycle events.");
        Assert(
            afterExplosion.Players.Single(player => player.PlayerId == 301).CurrentHealth < GameThreeSession.DefaultMaxHealth,
            "Bomb damage must be resolved by the server and reflected in a recovery snapshot.");
    }
    finally
    {
        game.Stop();
    }

    using var sessions = new GameSessionService();
    var started = sessions.StartSession(new[] { 401 }, GameModes.GameThree);
    Assert(
        started.Success
        && started.GameMode == GameModes.GameThree
        && started.SceneName == "GamePlayThree"
        && sessions.TryGetResumeState(401, out var resume)
        && resume is not null
        && resume.SnapshotPacket.ProtocolId == Protocols.GameThreeStateSync,
        "The default session service must register GamePlayThree and expose its recovery packet.");
}

static void VerifyGameThreeKinematicMotion()
{
    var first = new GameThreeHorizontalMotion(2f, 3f, 0f, 0f);
    var second = first;
    for (var step = 0; step < 20; step++)
    {
        GameThreeKinematicMotion.Step(ref first, 1f, 0.5f, 5f, 0.05f);
        GameThreeKinematicMotion.Step(ref second, 1f, 0.5f, 5f, 0.05f);
    }

    Assert(
        first.PositionX == second.PositionX
        && first.PositionZ == second.PositionZ
        && first.VelocityX == second.VelocityX
        && first.VelocityZ == second.VelocityZ,
        "Shared GamePlayThree movement must be deterministic for identical inputs.");
    Assert(
        MathF.Abs(MathF.Sqrt(first.VelocityX * first.VelocityX + first.VelocityZ * first.VelocityZ) - 5f) < 0.0001f,
        "Shared GamePlayThree movement must accelerate up to, but not beyond, its maximum speed.");

    var slowing = new GameThreeHorizontalMotion(0f, 0f, 3f, -2f);
    for (var step = 0; step < 20; step++)
    {
        GameThreeKinematicMotion.Step(ref slowing, 0f, 0f, 5f, 0.05f);
    }

    Assert(
        slowing.VelocityX == 0f && slowing.VelocityZ == 0f,
        "Shared GamePlayThree movement must apply friction until an idle ball stops.");
}

static void VerifyGameThreeInputAcknowledgement()
{
    var game = new GameThreeSession(
        startDelay: TimeSpan.Zero,
        tickInterval: TimeSpan.FromHours(1),
        roundDurationSeconds: 5f,
        randomSeed: 19);
    try
    {
        game.Start(new[] { 801 });
        Assert(
            game.HandleMessage(
                801,
                new BaseMessage(
                    Protocols.GameThreeInput,
                    MessageCodec.EncodeBody(new GameThreeInput
                    {
                        PlayerId = 801,
                        MoveX = 1f,
                        Sequence = 1,
                        DeltaTime = 0.05f
                    }))),
            "A valid GamePlayThree input must enter the authoritative processing queue.");

        var queuedState = MessageCodec.Decode<GameThreeStateSync>(game.CreateRecoverySnapshot(801))
            .Players.Single(player => player.PlayerId == 801);
        Assert(
            queuedState.LastProcessedInputSequence == 0,
            "A queued input must not be acknowledged before the authoritative simulation executes it.");

        var updatePlayers = typeof(GameThreeSession).GetMethod(
            "UpdatePlayersNoLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (updatePlayers is null)
        {
            throw new InvalidOperationException("GameThree input processing must remain available to the smoke test.");
        }

        updatePlayers.Invoke(game, new object[] { 0.05f, DateTime.UtcNow, new List<byte[]>() });
        var processedState = MessageCodec.Decode<GameThreeStateSync>(game.CreateRecoverySnapshot(801))
            .Players.Single(player => player.PlayerId == 801);
        Assert(
            processedState.LastProcessedInputSequence == 1 && processedState.VelocityX > 0f,
            "An input must be acknowledged only after it changes the authoritative simulation state.");

        var redundantFrames = new[]
        {
            new GameThreeInput { PlayerId = 801, MoveX = 1f, Sequence = 1, DeltaTime = 0.05f },
            new GameThreeInput { PlayerId = 801, MoveX = 1f, Sequence = 2, DeltaTime = 0.05f },
            new GameThreeInput { PlayerId = 801, MoveX = 1f, Sequence = 3, DeltaTime = 0.05f }
        };
        var acceptedRedundantFrame = false;
        foreach (var frame in redundantFrames)
        {
            acceptedRedundantFrame |= game.HandleMessage(
                801,
                new BaseMessage(Protocols.GameThreeInput, MessageCodec.EncodeBody(frame)));
        }
        Assert(
            acceptedRedundantFrame,
            "A redundant UDP input sequence must accept newer frames even when it repeats an older frame.");

        updatePlayers.Invoke(game, new object[] { 0.05f, DateTime.UtcNow, new List<byte[]>() });
        var recoveredState = MessageCodec.Decode<GameThreeStateSync>(game.CreateRecoverySnapshot(801))
            .Players.Single(player => player.PlayerId == 801);
        Assert(
            recoveredState.LastProcessedInputSequence == 3,
            "The authoritative session must process redundant UDP frames once and in sequence order.");
    }
    finally
    {
        game.Stop();
    }
}

static async Task VerifyGameThreeLlmAiLifecycle()
{
    var provider = new RecordingGameThreeAiDecisionProvider();
    var game = new GameThreeSession(
        startDelay: TimeSpan.Zero,
        tickInterval: TimeSpan.FromMilliseconds(5),
        roundDurationSeconds: 2f,
        aiDecisionProvider: provider);
    try
    {
        game.Start(new[] { 601 });
        await Task.Delay(80);
        Assert(provider.CallCount > 0, "An enabled LLM provider must receive decisions after a match starts.");
        Assert(!provider.ReceivedCancelledToken, "A new match must not inherit the cancellation token from Start's initial Stop call.");
    }
    finally
    {
        game.Stop();
    }
}

static async Task VerifyGameThreeUtilityAi()
{
    var utilityAi = new GameThreeUtilityAi(new GameThreeUtilityAiSettings
    {
        DecisionIntervalSeconds = 0.1f
    });
    var dangerObservation = CreateUtilityObservation(
        currentHealth: 100,
        isSkillReady: true,
        bombs: new[] { new GameThreeAiBomb(1, 1, 0, 0.4f) });
    var escapeDecision = utilityAi.Decide(dangerObservation);
    Assert(
        escapeDecision.Action == GameThreeUtilityAction.EscapeBomb
        && escapeDecision.TargetX <= dangerObservation.PositionX
        && escapeDecision.UseDash
        && escapeDecision.UseSkill,
        "Utility AI must move away from an imminent bomb and may spend defensive mobility and skill resources.");

    var pickupObservation = CreateUtilityObservation(
        currentHealth: 20,
        pickups: new[] { new GameThreeAiPickup("Health", -1, 0) });
    var pickupDecision = utilityAi.Decide(pickupObservation);
    Assert(
        pickupDecision.Action == GameThreeUtilityAction.CollectPickup
        && pickupDecision.TargetX < pickupObservation.PositionX,
        "Utility AI must prioritize an adjacent health pickup while critically wounded.");

    var recordingAi = new RecordingGameThreeTacticalAi();
    var game = new GameThreeSession(
        startDelay: TimeSpan.Zero,
        tickInterval: TimeSpan.FromMilliseconds(5),
        roundDurationSeconds: 2f,
        tacticalAi: recordingAi);
    try
    {
        game.Start(new[] { 701 });
        await Task.Delay(80);
        Assert(recordingAi.CallCount > 0, "The match loop must request Utility AI decisions for server bots.");
    }
    finally
    {
        game.Stop();
    }
}

static GameThreeAiObservation CreateUtilityObservation(
    int currentHealth,
    bool isSkillReady = false,
    IReadOnlyList<GameThreeAiBomb>? bombs = null,
    IReadOnlyList<GameThreeAiPickup>? pickups = null)
{
    return new GameThreeAiObservation(
        SlotIndex: 0,
        BallId: "sprinter",
        PositionX: 10.5f,
        PositionZ: 10.5f,
        CurrentHealth: currentHealth,
        MaxHealth: 100,
        Energy: isSkillReady ? 12 : 0,
        EnergyRequired: 12,
        TerritoryCount: 1,
        RoundTimeRemaining: 60f,
        Abilities: new GameThreeAiAbilityState(
            "ram",
            isSkillReady,
            string.Empty,
            0f,
            0f,
            0f,
            false),
        LocalMapRows: new[]
        {
            ".........",
            ".........",
            ".........",
            ".........",
            "....S....",
            ".........",
            ".........",
            ".........",
            "........."
        },
        VisibleOpponents: Array.Empty<GameThreeAiVisibleBall>(),
        NearbyBombs: bombs ?? Array.Empty<GameThreeAiBomb>(),
        NearbyPickups: pickups ?? Array.Empty<GameThreeAiPickup>(),
        NearbyMarkers: Array.Empty<GameThreeAiMarker>(),
        NearbyClones: Array.Empty<GameThreeAiClone>());
}

static void VerifyGameThreeLightweightPhysics()
{
    var game = new GameThreeSession(startDelay: TimeSpan.Zero, randomSeed: 17);
    try
    {
        game.Start(new[] { 501, 502 });
        var playersField = typeof(GameThreeSession).GetField("_players", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var solveMethod = typeof(GameThreeSession).GetMethod("ResolvePlayerCollisionsNoLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (playersField is null || solveMethod is null)
        {
            throw new InvalidOperationException("GameThree physics internals must remain available to the smoke test.");
        }
        var players = (Dictionary<int, GameThreeBallState>)playersField.GetValue(game)!;
        var first = players[501];
        var second = players[502];
        first.PositionX = first.PreviousPositionX = 8f;
        first.PositionZ = first.PreviousPositionZ = 8f;
        second.PositionX = second.PreviousPositionX = 8.3f;
        second.PositionZ = second.PreviousPositionZ = 8f;
        first.VelocityX = 5f;
        second.VelocityX = -4f;
        solveMethod.Invoke(game, new object[] { DateTime.UtcNow, new List<byte[]>() });
        var separation = MathF.Abs(second.PositionX - first.PositionX);
        Assert(
            separation >= 0.74f
            && first.KnockbackVelocityX < 0f
            && second.KnockbackVelocityX > 0f,
            "Overlapping balls must be separated and receive opposite mass-based impulses.");

        first.PreviousPositionX = 7f;
        first.PreviousPositionZ = 10f;
        second.PreviousPositionX = 11f;
        second.PreviousPositionZ = 10f;
        first.PositionX = 11.5f;
        first.PositionZ = 10f;
        second.PositionX = 6.5f;
        second.PositionZ = 10f;
        first.VelocityX = 8f;
        second.VelocityX = -8f;
        first.KnockbackVelocityX = second.KnockbackVelocityX = 0f;
        solveMethod.Invoke(game, new object[] { DateTime.UtcNow, new List<byte[]>() });
        Assert(
            first.PositionX < 11f && second.PositionX > 7f,
            "Fast balls that cross in one tick must resolve at swept contact rather than tunnel through each other.");
    }
    finally
    {
        game.Stop();
    }
}

static void VerifyClientGameModuleRegistry()
{
    var module = new ProbeClientGameModule();
    MyDemo.GameModules.ClientGameModuleRegistry.Register(module);
    Assert(
        MyDemo.GameModules.ClientGameModuleRegistry.TryGet(" PROBE_CLIENT ", out var resolved)
        && ReferenceEquals(module, resolved)
        && resolved.SceneName == ProbeClientGameModule.Scene,
        "A hot-update client module must be discoverable by normalized game mode.");
}

static string FindFromCurrentDirectory(string relativePath)
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, relativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    throw new FileNotFoundException($"Could not find {relativePath} from {Directory.GetCurrentDirectory()}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class ProbeGameSessionFactory : IGameSessionFactory
{
    public const string Mode = "probe_mode";
    public const string Scene = "ProbeGameplayScene";
    public string GameMode => Mode;
    public string SceneName => Scene;
    public int MaxPlayers => 2;
    public IGameSession Create() => new ProbeGameSession();
}

sealed class ProbeGameSession : IGameSession
{
    public const ushort CommandProtocol = 65000;
    public const ushort SnapshotProtocol = 65001;
    public string GameMode => ProbeGameSessionFactory.Mode;
    public string SceneName => ProbeGameSessionFactory.Scene;
    public event Action<byte[]>? Broadcast { add { } remove { } }
    public event Action? Ended { add { } remove { } }

    public void Start(IReadOnlyList<int> playerIds)
    {
    }

    public bool HandleMessage(int authenticatedPlayerId, BaseMessage message)
    {
        return message.ProtocolId == CommandProtocol;
    }

    public BaseMessage CreateRecoverySnapshot(int playerId)
    {
        return new BaseMessage(SnapshotProtocol, Array.Empty<byte>());
    }

    public void Stop()
    {
    }
}

sealed class RecordingGameThreeAiDecisionProvider : IGameThreeAiDecisionProvider
{
    public bool IsEnabled => true;
    public TimeSpan DecisionInterval => TimeSpan.FromMilliseconds(20);
    public TimeSpan MaximumDecisionAge => TimeSpan.FromSeconds(1);
    public int CallCount { get; private set; }
    public bool ReceivedCancelledToken { get; private set; }

    public Task<GameThreeAiDecision?> DecideAsync(GameThreeAiObservation observation, CancellationToken cancellationToken)
    {
        CallCount++;
        ReceivedCancelledToken |= cancellationToken.IsCancellationRequested;
        return Task.FromResult<GameThreeAiDecision?>(
            new GameThreeAiDecision(observation.PositionX + 1f, observation.PositionZ, false, false, DateTime.UtcNow));
    }
}

sealed class RecordingGameThreeTacticalAi : IGameThreeTacticalAi
{
    public bool IsEnabled => true;
    public TimeSpan DecisionInterval => TimeSpan.FromMilliseconds(10);
    public int CallCount { get; private set; }

    public GameThreeTacticalDecision Decide(GameThreeAiObservation observation)
    {
        CallCount++;
        return new GameThreeTacticalDecision(
            observation.PositionX + 1f,
            observation.PositionZ,
            false,
            false,
            false,
            GameThreeUtilityAction.Explore,
            1f,
            "test");
    }
}

sealed class ProbeClientGameModule : MyDemo.GameModules.IClientGameModule
{
    public const string Scene = "ProbeClientScene";
    public string GameMode => "probe_client";
    public string SceneName => Scene;
    public void Prepare()
    {
    }
}

sealed class ProbeAccountService : IAccountService
{
    public Task<AccountAuthenticationResult> AuthenticateAsync(string username, string password)
    {
        return Task.FromResult(new AccountAuthenticationResult(false, "Not used by smoke tests", 0));
    }

    public Task<AccountRegistrationResult> RegisterAsync(string username, string password)
    {
        return Task.FromResult(new AccountRegistrationResult(false, "Not used by smoke tests"));
    }
}
