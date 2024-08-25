﻿using Microsoft.EntityFrameworkCore;
using Moonlight.App.ApiClients.Wings;
using Moonlight.App.ApiClients.Wings.Requests;
using Moonlight.App.ApiClients.Wings.Resources;
using Moonlight.App.Database;
using Moonlight.App.Database.Entities;
using Moonlight.App.Events;
using Moonlight.App.Exceptions;
using Moonlight.App.Helpers;
using Moonlight.App.Helpers.Files;
using Moonlight.App.Helpers.Wings;
using Moonlight.App.Models.Misc;
using Moonlight.App.Repositories;
using Moonlight.App.Repositories.Servers;
using Moonlight.App.Services.Background;
using Moonlight.App.Services.Plugins;
using FileAccess = Moonlight.App.Helpers.Files.FileAccess;

namespace Moonlight.App.Services;

public class ServerService
{
    private readonly Repository<ServerVariable> ServerVariablesRepository;
    private readonly ServerRepository ServerRepository;
    private readonly UserRepository UserRepository;
    private readonly ImageRepository ImageRepository;
    private readonly NodeRepository NodeRepository;
    private readonly NodeAllocationRepository NodeAllocationRepository;
    private readonly WingsApiHelper WingsApiHelper;
    private readonly UserService UserService;
    private readonly ConfigService ConfigService;
    private readonly WingsJwtHelper WingsJwtHelper;
    private readonly NodeService NodeService;
    private readonly DateTimeService DateTimeService;
    private readonly EventSystem Event;

    // We inject the dependencies for the malware scan service here because otherwise it may result in
    // a circular dependency injection which will crash the app
    private readonly PluginService PluginService;
    private readonly IServiceScopeFactory ServiceScopeFactory;

    public ServerService(
        ServerRepository serverRepository,
        WingsApiHelper wingsApiHelper,
        UserRepository userRepository,
        ImageRepository imageRepository,
        NodeRepository nodeRepository,
        UserService userService,
        ConfigService configService,
        WingsJwtHelper wingsJwtHelper,
        NodeService nodeService,
        NodeAllocationRepository nodeAllocationRepository,
        DateTimeService dateTimeService,
        EventSystem eventSystem,
        Repository<ServerVariable> serverVariablesRepository,
        PluginService pluginService,
        IServiceScopeFactory serviceScopeFactory)
    {
        ServerRepository = serverRepository;
        WingsApiHelper = wingsApiHelper;
        UserRepository = userRepository;
        ImageRepository = imageRepository;
        NodeRepository = nodeRepository;
        UserService = userService;
        ConfigService = configService;
        WingsJwtHelper = wingsJwtHelper;
        NodeService = nodeService;
        NodeAllocationRepository = nodeAllocationRepository;
        DateTimeService = dateTimeService;
        Event = eventSystem;
        ServerVariablesRepository = serverVariablesRepository;
        PluginService = pluginService;
        ServiceScopeFactory = serviceScopeFactory;
    }

    private Server EnsureNodeData(Server s)
    {
        if (s.Node == null) // Ensure node data is available
        {
            return ServerRepository
                .Get()
                .Include(x => x.Node)
                .First(x => x.Id == s.Id);
        }
        else
            return s;
    }

    public async Task<ServerDetails> GetDetails(Server s)
    {
        Server server = EnsureNodeData(s);

        ServerDetails result = null!;

        await new Retry()
            .Times(3)
            .At(x => x.Message.Contains("A task was canceled"))
            .Call(async () =>
            {
                result = await WingsApiHelper.Get<ServerDetails>(
                    server.Node,
                    $"api/servers/{server.Uuid}"
                );
            });

        return result;
    }

    public async Task SetPowerState(Server s, PowerSignal signal)
    {
        Server server = EnsureNodeData(s);

        if (ConfigService.Get().Moonlight.Security.MalwareCheckOnStart && signal == PowerSignal.Start ||
            signal == PowerSignal.Restart)
        {
            var result = await new MalwareScanService(
                PluginService,
                ServiceScopeFactory
            ).Perform(server);

            if (result != null)
            {
                Logger.Warn($"Found malware on server {server.Uuid}. Result: " + result.Title, "security");

                throw new DisplayException(
                    $"Unable to start server. Found following malware on this server: {result.Title}. Please contact the support if you think this detection is a false positive",
                    true);
            }
        }

        var rawSignal = signal.ToString().ToLower();

        await WingsApiHelper.Post(server.Node, $"api/servers/{server.Uuid}/power", new ServerPower()
        {
            Action = rawSignal
        });

        //TODO: AuditLog
    }

    public async Task<ServerBackup> CreateBackup(Server server)
    {
        var serverData = ServerRepository // Ensure data
            .Get()
            .Include(x => x.Node)
            .Include(x => x.Backups)
            .First(x => x.Id == server.Id);

        var backup = new ServerBackup()
        {
            Name =
                $"Created at {DateTimeService.GetCurrent().ToShortDateString()} {DateTimeService.GetCurrent().ToShortTimeString()}",
            Uuid = Guid.NewGuid(),
            CreatedAt = DateTimeService.GetCurrent(),
            Created = false
        };

        serverData.Backups.Add(backup);
        ServerRepository.Update(serverData);

        await WingsApiHelper.Post(serverData.Node, $"api/servers/{serverData.Uuid}/backup", new CreateBackup()
        {
            Adapter = "wings",
            Uuid = backup.Uuid,
            Ignore = ""
        });

        //TODO: AuditLog

        return backup;
    }

    public Task<ServerBackup[]> GetBackups(Server server, bool forceReload = false)
    {
        if (forceReload) //TODO: Find an alternative to avoid cache and the creation of a new db context
        {
            var serverData = new ServerRepository(new DataContext(ConfigService))
                .Get()
                .Include(x => x.Backups)
                .First(x => x.Id == server.Id);

            return Task.FromResult(serverData.Backups.ToArray());
        }
        else
        {
            var serverData = ServerRepository
                .Get()
                .Include(x => x.Backups)
                .First(x => x.Id == server.Id);

            return Task.FromResult(serverData.Backups.ToArray());
        }
    }

    public async Task RestoreBackup(Server s, ServerBackup serverBackup)
    {
        Server server = EnsureNodeData(s);

        await WingsApiHelper.Post(server.Node, $"api/servers/{server.Uuid}/backup/{serverBackup.Uuid}/restore",
            new RestoreBackup()
            {
                Adapter = "wings"
            });

        //TODO: AuditLog
    }

    public async Task DeleteBackup(Server server, ServerBackup serverBackup)
    {
        var serverData = ServerRepository
            .Get()
            .Include(x => x.Node)
            .Include(x => x.Backups)
            .First(x => x.Id == server.Id);

        try
        {
            await new Retry()
                .Times(3)
                .At(x => x.Message.Contains("A task was canceled"))
                .Call(async () =>
                {
                    await WingsApiHelper.Delete(serverData.Node,
                        $"api/servers/{serverData.Uuid}/backup/{serverBackup.Uuid}",
                        null);
                });
        }
        catch (WingsException e)
        {
            // when a backup is not longer there we can
            // safely delete the backup so we ignore this error
            if (e.StatusCode != 404)
            {
                throw;
            }
        }

        var backup = serverData.Backups.First(x => x.Uuid == serverBackup.Uuid);
        serverData.Backups.Remove(backup);

        ServerRepository.Update(serverData);

        await Event.Emit("wings.backups.delete", backup);

        //TODO: AuditLog
    }

    public async Task<string> DownloadBackup(Server s, ServerBackup serverBackup)
    {
        Server server = EnsureNodeData(s);

        var token = WingsJwtHelper.Generate(server.Node.Token, claims =>
        {
            claims.Add("server_uuid", server.Uuid.ToString());
            claims.Add("backup_uuid", serverBackup.Uuid.ToString());
        });

        //TODO: AuditLog

        if (server.Node.Ssl)
            return $"https://{server.Node.Fqdn}:{server.Node.HttpPort}/download/backup?token={token}";
        else
            return $"http://{server.Node.Fqdn}:{server.Node.HttpPort}/download/backup?token={token}";
    }

    public Task<FileAccess> CreateFileAccess(Server s, User user) // We need the user to create the launch url
    {
        Server server = EnsureNodeData(s);

        return Task.FromResult(
            (FileAccess)new WingsFileAccess(
                WingsApiHelper,
                WingsJwtHelper,
                server,
                ConfigService,
                user
            )
        );
    }

    public async Task<Server> Create(string name, int cpu, long memory, long disk, User u, Image i, Node? n = null,
        Action<Server>? modifyDetails = null)
    {
        var user = UserRepository
            .Get()
            .First(x => x.Id == u.Id);

        var image = ImageRepository
            .Get()
            .Include(x => x.Variables)
            .Include(x => x.DockerImages)
            .First(x => x.Id == i.Id);

        var allocations = image.Allocations;

        Node node = n ?? NodeRepository.Get().First();

        NodeAllocation[] freeAllocations;

        try
        {
            // We have sadly no choice to use entity framework to do what the sql call does, there
            // are only slower ways, so we will use a raw sql call as a exception

            freeAllocations = NodeAllocationRepository
                .Get()
                .FromSqlRaw(
                    $"SELECT * FROM `NodeAllocations` WHERE ServerId IS NULL AND NodeId={node.Id} LIMIT {allocations}")
                .ToArray();
        }
        catch (Exception)
        {
            throw new DisplayException("No allocation found");
        }

        if (!freeAllocations.Any())
            throw new DisplayException("No allocation found");

        if (freeAllocations.Length != allocations)
            throw new DisplayException("Not enough allocations found");

        var server = new Server()
        {
            Cpu = cpu,
            Memory = memory,
            Disk = disk,
            Name = name,
            Image = image,
            Owner = user,
            Node = node,
            Uuid = Guid.NewGuid(),
            MainAllocation = freeAllocations.First(),
            Allocations = freeAllocations.ToList(),
            Backups = new(),
            OverrideStartup = "",
            DockerImageIndex = image.DockerImages.FindIndex(x => x.Default),
            Installing = true
        };

        foreach (var imageVariable in image.Variables)
        {
            server.Variables.Add(new()
            {
                Key = imageVariable.Key,
                Value = imageVariable.DefaultValue
            });
        }

        if (modifyDetails != null)
            modifyDetails.Invoke(server);

        var newServerData = ServerRepository.Add(server);

        try
        {
            await new Retry()
                .Times(3)
                .At(x => x.Message.Contains("A task was canceled"))
                .Call(async () =>
                {
                    await WingsApiHelper.Post(node, $"api/servers", new CreateServer()
                    {
                        Uuid = newServerData.Uuid,
                        StartOnCompletion = false
                    });
                });

            //TODO: AuditLog

            return newServerData;
        }
        catch (Exception e)
        {
            Logger.Error("Error creating server on wings");
            Logger.Error(e);

            ServerRepository.Delete(newServerData); //TODO Remove unsinged table stuff

            throw new DisplayException("Error creating server on wings");
        }
    }

    public async Task Reinstall(Server s)
    {
        Server server = EnsureNodeData(s);

        await WingsApiHelper.Post(server.Node, $"api/servers/{server.Uuid}/reinstall", null);

        server.Installing = true;
        ServerRepository.Update(server);

        //TODO: AuditLog
    }

    public async Task<Server> SftpServerLogin(int serverId, int id, string password)
    {
        var server = ServerRepository.Get().FirstOrDefault(x => x.Id == serverId);

        if (server == null)
        {
            Logger.Warn($"Detected an sftp bruteforce attempt. ID: {id} Password: {password}", "security");
            throw new Exception("Server not found");
        }

        var user = await UserService.SftpLogin(id, password);

        if (server.Owner.Id == user.Id || user.Admin)
        {
            return server;
        }
        else
        {
            //TODO: Decide if logging
            throw new Exception("User and owner id do not match");
        }
    }

    public async Task Sync(Server s)
    {
        var server = EnsureNodeData(s);

        await WingsApiHelper.Post(server.Node, $"api/servers/{server.Uuid}/sync", null);
    }

    public async Task Delete(Server s)
    {
        var backups = await GetBackups(s);

        foreach (var backup in backups)
        {
            try
            {
                await DeleteBackup(s, backup);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        var server = ServerRepository
            .Get()
            .Include(x => x.Variables)
            .Include(x => x.Node)
            .First(x => x.Id == s.Id);

        try
        {
            await new Retry()
                .Times(3)
                .At(x => x.Message.Contains("A task was canceled"))
                .Call(async () => { await WingsApiHelper.Delete(server.Node, $"api/servers/{server.Uuid}", null); });
        }
        catch (WingsException e)
        {
            if (e.StatusCode != 404)
                throw;
        }

        foreach (var variable in server.Variables.ToArray())
        {
            ServerVariablesRepository.Delete(variable);
        }

        server.Allocations = new();
        server.MainAllocation = null;
        server.Variables = new();
        server.Backups = new();

        ServerRepository.Update(server);
        ServerRepository.Delete(server);
    }

    public async Task<bool> IsHostUp(Server s)
    {
        var server = EnsureNodeData(s);

        return await NodeService.IsHostUp(server.Node);
    }

    public async Task ArchiveServer(Server server)
    {
        if (server.IsArchived)
            throw new DisplayException("Unable to archive an already archived server");

        // Archive server

        var backup = await CreateBackup(server);
        server.IsArchived = true;
        server.Archive = backup;

        ServerRepository.Update(server);

        await Event.WaitForEvent<ServerBackup>("wings.backups.create", this, x => backup.Id == x.Id);

        // Reset server

        var access = await CreateFileAccess(server, null!);
        var files = await access.Ls();
        foreach (var file in files)
        {
            try
            {
                await access.Delete(file);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        await Event.Emit($"server.{server.Uuid}.archiveStatusChanged", server);
    }

    public async Task UnArchiveServer(Server s)
    {
        if (!s.IsArchived)
            throw new DisplayException("Unable to unarchive a server which is not archived");

        var server = ServerRepository
            .Get()
            .Include(x => x.Archive)
            .First(x => x.Id == s.Id);

        if (server.Archive == null)
            throw new DisplayException("Archive from server not found");

        if (!server.Archive.Created)
            throw new DisplayException("Creating the server archive is in progress");

        await RestoreBackup(server, server.Archive);

        await Event.WaitForEvent<ServerBackup>("wings.backups.restore", this,
            x => x.Id == server.Archive.Id);

        server.IsArchived = false;
        ServerRepository.Update(server);

        await Event.Emit($"server.{server.Uuid}.archiveStatusChanged", server);
    }
}