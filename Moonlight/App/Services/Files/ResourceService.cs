﻿using Moonlight.App.Database.Entities;

namespace Moonlight.App.Services.Files;

public class ResourceService
{
    private readonly string AppUrl;
    
    public ResourceService(ConfigService configService)
    {
        AppUrl = configService.Get().Moonlight.AppUrl;
    }

    public string Image(string name)
    {
        return $"{AppUrl}/api/moonlight/resources/images/{name}";
    }
    
    public string BackgroundImage(string name)
    {
        return $"{AppUrl}/api/moonlight/resources/background/{name}";
    }

    public string Avatar(User user)
    {
        return $"{AppUrl}/api/moonlight/avatar/{user.Id}";
    }

    public string BucketItem(string bucket, string name)
    {
        return $"{AppUrl}/api/moonlight/resources/bucket/{bucket}/{name}";
    }
}