﻿using MimeKit;
using Moonlight.App.Database.Entities;
using Moonlight.App.Exceptions;
using Moonlight.App.Helpers;
using Moonlight.App.Repositories;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace Moonlight.App.Services.Mail;

public class MailService
{
    private readonly string Server;
    private readonly string Password;
    private readonly string Email;
    private readonly int Port;
    private readonly bool Ssl;

    private readonly Repository<User> UserRepository;

    public MailService(
        ConfigService configService,
        Repository<User> userRepository)
    {
        UserRepository = userRepository;
        
        var mailConfig = configService
            .Get()
            .Moonlight.Mail;

        Server = mailConfig.Server;
        Password = mailConfig.Password;
        Email = mailConfig.Email;
        Port = mailConfig.Port;
        Ssl = mailConfig.Ssl;
    }

    public Task SendMailRaw(User user, string html)
    {
        Task.Run(async () =>
        {
            try
            {
                using var client = new SmtpClient();

                var mailMessage = new MimeMessage();
                mailMessage.From.Add(new MailboxAddress(Email, Email));
                mailMessage.To.Add(new MailboxAddress(user.Email, user.Email));
                mailMessage.Subject = $"Hey {user.FirstName}, there are news from moonlight";
                
                var body = new BodyBuilder
                {
                    HtmlBody = html
                };
                mailMessage.Body = body.ToMessageBody();

                using var smtpClient = new SmtpClient();
                await smtpClient.ConnectAsync(Server, Port, Ssl);
                await smtpClient.AuthenticateAsync(Email, Password);
                await smtpClient.SendAsync(mailMessage);
                await smtpClient.DisconnectAsync(true);
            }
            catch (Exception e)
            {
                Logger.Warn("Error sending mail");
                Logger.Warn(e);
            }
        });
        
        return Task.CompletedTask;
    }
    
    public async Task SendMail(User user, string template, Action<Dictionary<string, string>> values)
    {
        if (!File.Exists(PathBuilder.File("storage", "resources", "mail", $"{template}.html")))
        {
            Logger.Warn($"Mail template '{template}' not found. Make sure to place one in the resources folder");
            throw new DisplayException("Mail template not found");
        }

        var rawHtml = await File.ReadAllTextAsync(PathBuilder.File("storage", "resources", "mail", $"{template}.html"));

        var val = new Dictionary<string, string>();
        values.Invoke(val);
        
        val.Add("FirstName", user.FirstName);
        val.Add("LastName", user.LastName);

        var parsed = ParseMail(rawHtml, val);

        await SendMailRaw(user, parsed);
    }

    public async Task SendEmailToAll(string template, Action<Dictionary<string, string>> values)
    {
        var users = UserRepository
            .Get()
            .ToArray();

        foreach (var user in users)
        {
            await SendMail(user, template, values);
        }
    }

    public async Task SendEmailToAllAdmins(string template, Action<Dictionary<string, string>> values)
    {
        var users = UserRepository
            .Get()
            .Where(x => x.Admin)
            .ToArray();

        foreach (var user in users)
        {
            await SendMail(user, template, values);
        }
    }

    private string ParseMail(string html, Dictionary<string, string> values)
    {
        foreach (var kvp in values)
        {
            html = html.Replace("{{" + kvp.Key + "}}", kvp.Value);
        }

        return html;
    }
}