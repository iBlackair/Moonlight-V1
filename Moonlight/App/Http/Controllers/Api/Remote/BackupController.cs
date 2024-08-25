﻿using Microsoft.AspNetCore.Mvc;
using Moonlight.App.Events;
using Moonlight.App.Http.Requests.Wings;
using Moonlight.App.Repositories;
using Moonlight.App.Repositories.Servers;
using Moonlight.App.Services;

namespace Moonlight.App.Http.Controllers.Api.Remote;

[Route("api/remote/backups")]
[ApiController]
public class BackupController : Controller
{
    private readonly ServerBackupRepository ServerBackupRepository;
    private readonly EventSystem Event;
    private readonly NodeRepository NodeRepository;

    public BackupController(
        ServerBackupRepository serverBackupRepository, 
        NodeRepository nodeRepository,
        EventSystem eventSystem)
    {
        ServerBackupRepository = serverBackupRepository;
        NodeRepository = nodeRepository;
        Event = eventSystem;
    }

    [HttpGet("{uuid}")]
    public ActionResult<string> Download(Guid uuid)
    {
        return "";
    }

    [HttpPost("{uuid}")]
    public async Task<ActionResult> SetStatus([FromRoute] Guid uuid, [FromBody] ReportBackupCompleteRequest request)
    {
        var tokenData = Request.Headers.Authorization.ToString().Replace("Bearer ", "");
        var id = tokenData.Split(".")[0];
        var token = tokenData.Split(".")[1];

        var node = NodeRepository.Get().FirstOrDefault(x => x.TokenId == id);

        if (node == null)
            return NotFound();
        
        if (token != node.Token)
            return Unauthorized();
        
        var backup = ServerBackupRepository.Get().FirstOrDefault(x => x.Uuid == uuid);
        
        if (backup == null)
            return NotFound();

        if (request.Successful)
        {
            backup.Created = true;
            backup.Bytes = request.Size;
            
            ServerBackupRepository.Update(backup);
            
            await Event.Emit($"wings.backups.create", backup);
        }
        else
        {
            await Event.Emit($"wings.backups.createFailed", backup);
            ServerBackupRepository.Delete(backup);
        }

        return NoContent();
    }

    [HttpPost("{uuid}/restore")]
    public async Task<ActionResult> SetRestoreStatus([FromRoute] Guid uuid)
    {
        var tokenData = Request.Headers.Authorization.ToString().Replace("Bearer ", "");
        var id = tokenData.Split(".")[0];
        var token = tokenData.Split(".")[1];

        var node = NodeRepository.Get().FirstOrDefault(x => x.TokenId == id);

        if (node == null)
            return NotFound();
        
        if (token != node.Token)
            return Unauthorized();
        
        var backup = ServerBackupRepository.Get().FirstOrDefault(x => x.Uuid == uuid);
        
        if (backup == null)
            return NotFound();
        
        await Event.Emit($"wings.backups.restore", backup);

        return NoContent();
    }
}