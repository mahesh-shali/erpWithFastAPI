using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Erp.Api.Contracts;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

/// <summary>
/// End-to-end encrypted chat: the server only sees ciphertext, so these tests use random bytes of the right
/// shape and focus on who may talk to whom. The crypto itself is tested in frontend/src/chat/crypto.test.ts.
/// </summary>
[Collection(ApiCollection.Name)]
public class ChatTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";

    private sealed record Person(HttpClient Client, Guid Id, string Name);

    private sealed class Org
    {
        public required Person Owner { get; init; }
        public required Person Admin { get; init; }
        public required Person UserA { get; init; }
        public required Person UserB { get; init; }
    }

    private async Task<Org> NewOrg()
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Chat Org " + Guid.NewGuid().ToString("N")[..6], $"owner{Guid.NewGuid():N}@x.com", Pw, "Owner"));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var owner = new Person(c, auth.User.Id, auth.User.FullName);
        return new Org
        {
            Owner = owner,
            Admin = await Create(owner.Client, "Admin"),
            UserA = await Create(owner.Client, "User"),
            UserB = await Create(owner.Client, "User"),
        };
    }

    private async Task<Person> Create(HttpClient creator, string role)
    {
        var email = $"{role.ToLowerInvariant()}{Guid.NewGuid():N}@x.com";
        var res = await creator.PostAsJsonAsync("/api/users", new CreateUserRequest(email, $"{role} {email[..10]}", Pw, role));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var user = (await res.Content.ReadFromJsonAsync<OrgUserDto>())!;
        var c = factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Pw));
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Person(c, user.Id, user.FullName);
    }

    private static string B64(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    private static RegisterChatKeyRequest NewKey()
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new RegisterChatKeyRequest(Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()), B64(12 + 138 + 16), B64(16), 600_000);
    }

    private static async Task<ChatKeyBundleDto> SetUpKey(Person p)
    {
        var res = await p.Client.PostAsJsonAsync("/api/chat/keys", NewKey());
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ChatKeyBundleDto>())!;
    }

    private static async Task<ChatConversationDto> Open(Person p, string? title, params Person[] with)
    {
        var res = await p.Client.PostAsJsonAsync("/api/chat/conversations", new CreateConversationRequest(with.Select(w => w.Id).ToList(), title));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<ChatConversationDto>())!;
    }

    /// <summary>Encrypts (with random bytes) for exactly the recipients the server lists, like the real client.</summary>
    private static async Task<HttpResponseMessage> Send(Person p, Guid conversationId, Guid senderKeyId, Func<List<ChatWrappedKeyDto>, List<ChatWrappedKeyDto>>? tamper = null)
    {
        var recipients = (await p.Client.GetFromJsonAsync<ChatRecipientsDto>($"/api/chat/conversations/{conversationId}/recipients"))!;
        var keys = recipients.Recipients.Select(r => new ChatWrappedKeyDto(r.KeyId!.Value, B64(60))).ToList();
        return await p.Client.PostAsJsonAsync($"/api/chat/conversations/{conversationId}/messages",
            new SendChatMessageRequest(senderKeyId, B64(64), B64(12), tamper?.Invoke(keys) ?? keys));
    }

    private static async Task<List<ChatConversationDto>> Conversations(Person p) =>
        (await p.Client.GetFromJsonAsync<List<ChatConversationDto>>("/api/chat/conversations"))!;

    [Fact]
    public async Task Members_exchange_messages_that_are_only_stored_as_ciphertext_with_a_key_per_recipient()
    {
        var org = await NewOrg();
        var aKey = await SetUpKey(org.UserA);
        var bKey = await SetUpKey(org.UserB);

        var conv = await Open(org.UserA, null, org.UserB);
        conv.Kind.Should().Be("direct");
        conv.Members.Select(m => m.UserId).Should().BeEquivalentTo([org.UserA.Id, org.UserB.Id]);

        var sent = await Send(org.UserA, conv.Id, aKey.KeyId);
        sent.StatusCode.Should().Be(HttpStatusCode.OK);
        var msg = (await sent.Content.ReadFromJsonAsync<ChatMessageDto>())!;

        var bList = await Conversations(org.UserB);
        bList.Single(c => c.Id == conv.Id).Unread.Should().Be(1);

        var bMessages = (await org.UserB.Client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/chat/conversations/{conv.Id}/messages"))!;
        var got = bMessages.Single();
        got.Ciphertext.Should().Be(msg.Ciphertext);
        got.SenderKeyId.Should().Be(aKey.KeyId);
        got.SenderPublicKey.Should().Be(aKey.PublicKey);
        got.WrappedKey.Should().NotBeNull().And.NotBe(msg.WrappedKey, "every recipient key gets its own wrapped copy");

        (await org.UserB.Client.PostAsJsonAsync($"/api/chat/conversations/{conv.Id}/read", new MarkReadRequest(got.Id)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Conversations(org.UserB)).Single(c => c.Id == conv.Id).Unread.Should().Be(0);

        // Opening the same direct chat again (from either side) returns the existing conversation
        (await Open(org.UserB, null, org.UserA)).Id.Should().Be(conv.Id);
        _ = bKey;
    }

    [Fact]
    public async Task A_message_must_be_encrypted_for_exactly_the_members_current_keys()
    {
        var org = await NewOrg();
        var aKey = await SetUpKey(org.UserA);
        await SetUpKey(org.UserB);
        var adminKey = await SetUpKey(org.Admin);
        var conv = await Open(org.UserA, null, org.UserB);

        // Leaving a member out
        (await Send(org.UserA, conv.Id, aKey.KeyId, keys => keys.Where(k => k.RecipientKeyId == aKey.KeyId).ToList()))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Slipping in an outsider's key
        (await Send(org.UserA, conv.Id, aKey.KeyId, keys => [.. keys, new ChatWrappedKeyDto(adminKey.KeyId, B64(60))]))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Malformed wrapped key
        (await Send(org.UserA, conv.Id, aKey.KeyId, keys => keys.Select(k => k with { WrappedKey = "not-base64!" }).ToList()))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Stale sender key
        (await Send(org.UserA, conv.Id, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static async Task GrantSeniorMessaging(Org org, Person p)
    {
        var pageId = (await org.Owner.Client.GetFromJsonAsync<List<PageDto>>("/api/master/pages"))!.Single(x => x.Key == "messages-senior").Id;
        (await org.Owner.Client.PutAsJsonAsync($"/api/master/users/{p.Id}/rights",
                new SetRightsRequest([new SetRightsItem(pageId, false, new PageRightsDto(false, false, false, true, false, false))])))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SuperAdmin_takes_part_in_member_conversations_and_juniors_can_reply()
    {
        var org = await NewOrg();
        var ownerKey = await SetUpKey(org.Owner);
        var aKey = await SetUpKey(org.UserA);

        var contacts = (await org.Owner.Client.GetFromJsonAsync<List<ChatContactDto>>("/api/chat/contacts"))!;
        contacts.Select(c => c.UserId).Should().BeEquivalentTo([org.Admin.Id, org.UserA.Id, org.UserB.Id]);

        var conv = await Open(org.Owner, null, org.UserA);
        (await Send(org.Owner, conv.Id, ownerKey.KeyId)).StatusCode.Should().Be(HttpStatusCode.OK);

        // UserA has no right to start chats with senior roles, but may answer in a chat the owner started
        var seenByA = (await Conversations(org.UserA)).Single(c => c.Id == conv.Id);
        seenByA.CanPost.Should().BeTrue();
        (await Open(org.UserA, null, org.Owner)).Id.Should().Be(conv.Id);
        (await Send(org.UserA, conv.Id, aKey.KeyId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var g = await Open(org.Owner, "Leadership", org.Admin, org.UserA);
        g.Members.Should().HaveCount(3);
        (await Conversations(org.Owner)).Select(c => c.Kind).Should().Contain(["general", "announcement", "direct", "group"]);
    }

    [Fact]
    public async Task Starting_a_chat_with_a_more_senior_role_needs_the_senior_messaging_right()
    {
        var org = await NewOrg();
        var manager = await Create(org.Owner.Client, "Manager");

        // User (level 10): only same level or below by default
        var contacts = (await org.UserA.Client.GetFromJsonAsync<List<ChatContactDto>>("/api/chat/contacts"))!;
        contacts.Select(c => c.UserId).Should().BeEquivalentTo([org.UserB.Id]);
        foreach (var senior in new[] { org.Owner, org.Admin, manager })
            (await org.UserA.Client.PostAsJsonAsync("/api/chat/conversations", new CreateConversationRequest([senior.Id], null)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await org.UserA.Client.PostAsJsonAsync("/api/chat/conversations", new CreateConversationRequest([org.UserB.Id, manager.Id], "Team")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await Open(org.UserA, null, org.UserB);

        // Admin and Manager have the right by default
        (await Open(org.Admin, null, org.Owner)).Kind.Should().Be("direct");
        (await Open(manager, "Escalations", org.Admin, org.Owner)).Kind.Should().Be("group");

        // Granting it to a user unlocks senior contacts
        await GrantSeniorMessaging(org, org.UserA);
        (await org.UserA.Client.GetFromJsonAsync<List<ChatContactDto>>("/api/chat/contacts"))!
            .Select(c => c.UserId).Should().BeEquivalentTo([org.Owner.Id, org.Admin.Id, org.UserB.Id, manager.Id]);
        (await Open(org.UserA, null, org.Admin)).Kind.Should().Be("direct");
    }

    [Fact]
    public async Task Everyone_in_the_organization_posts_in_the_General_channel()
    {
        var org = await NewOrg();
        await SetUpKey(org.Owner);
        var aKey = await SetUpKey(org.UserA);
        await SetUpKey(org.Admin);
        // UserB has not set up secure messaging yet

        var general = (await Conversations(org.UserA)).Single(c => c.Kind == "general");
        general.Title.Should().Be("General");
        general.CanPost.Should().BeTrue();
        var recipients = (await org.UserA.Client.GetFromJsonAsync<ChatRecipientsDto>($"/api/chat/conversations/{general.Id}/recipients"))!;
        recipients.Recipients.Should().HaveCount(3);
        recipients.WithoutKey.Should().ContainSingle();

        (await Send(org.UserA, general.Id, aKey.KeyId)).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var p in new[] { org.Owner, org.Admin, org.UserB })
        {
            var seen = (await Conversations(p)).Single(c => c.Kind == "general");
            seen.Id.Should().Be(general.Id);
            seen.Unread.Should().Be(1);
            seen.CanPost.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Only_the_SuperAdmin_posts_announcements_and_every_member_receives_them()
    {
        var org = await NewOrg();
        var ownerKey = await SetUpKey(org.Owner);
        var aKey = await SetUpKey(org.UserA);
        await SetUpKey(org.Admin);
        // UserB has not set up secure messaging yet

        var ann = (await Conversations(org.Owner)).Single(c => c.Kind == "announcement");
        ann.CanPost.Should().BeTrue();
        var recipients = (await org.Owner.Client.GetFromJsonAsync<ChatRecipientsDto>($"/api/chat/conversations/{ann.Id}/recipients"))!;
        recipients.Recipients.Should().HaveCount(3);
        recipients.WithoutKey.Should().ContainSingle();

        (await Send(org.Owner, ann.Id, ownerKey.KeyId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var seenByA = (await Conversations(org.UserA)).Single(c => c.Kind == "announcement");
        seenByA.Id.Should().Be(ann.Id);
        seenByA.CanPost.Should().BeFalse();
        seenByA.Unread.Should().Be(1);
        var msgs = (await org.UserA.Client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/chat/conversations/{ann.Id}/messages"))!;
        msgs.Single().WrappedKey.Should().NotBeNull();

        (await org.UserA.Client.GetAsync($"/api/chat/conversations/{ann.Id}/recipients")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await org.UserA.Client.PostAsJsonAsync($"/api/chat/conversations/{ann.Id}/messages",
                new SendChatMessageRequest(aKey.KeyId, B64(64), B64(12), [new ChatWrappedKeyDto(aKey.KeyId, B64(60))])))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Conversations_are_private_to_their_members_and_to_the_organization()
    {
        var org = await NewOrg();
        var other = await NewOrg();
        await SetUpKey(org.UserA);
        var conv = await Open(org.UserA, null, org.UserB);

        // Same org, not a member
        (await Conversations(org.Admin)).Should().NotContain(c => c.Id == conv.Id);
        (await org.Admin.Client.GetAsync($"/api/chat/conversations/{conv.Id}/messages")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Another tenant
        (await other.UserA.Client.GetAsync($"/api/chat/conversations/{conv.Id}/messages")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.UserA.Client.PostAsJsonAsync("/api/chat/conversations", new CreateConversationRequest([org.UserA.Id], null)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Each org has its own announcements channel
        (await Conversations(other.UserA)).Single(c => c.Kind == "announcement").Id
            .Should().NotBe((await Conversations(org.UserA)).Single(c => c.Kind == "announcement").Id);
    }

    [Fact]
    public async Task Groups_need_a_name_and_include_every_member()
    {
        var org = await NewOrg();
        (await org.Admin.Client.PostAsJsonAsync("/api/chat/conversations", new CreateConversationRequest([org.UserA.Id, org.UserB.Id], " ")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var g = await Open(org.Admin, "Shift leads", org.UserA, org.UserB);
        g.Kind.Should().Be("group");
        g.Title.Should().Be("Shift leads");
        g.Members.Should().HaveCount(3);
        (await Conversations(org.UserB)).Should().Contain(c => c.Id == g.Id);
    }

    [Fact]
    public async Task Key_setup_is_once_and_a_reset_makes_old_messages_unreadable()
    {
        var org = await NewOrg();
        var aKey = await SetUpKey(org.UserA);
        await SetUpKey(org.UserB);
        (await org.UserA.Client.PostAsJsonAsync("/api/chat/keys", NewKey())).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await org.UserA.Client.GetFromJsonAsync<ChatKeyBundleDto>("/api/chat/keys/me"))!.KeyId.Should().Be(aKey.KeyId);

        var conv = await Open(org.UserA, null, org.UserB);
        (await Send(org.UserA, conv.Id, aKey.KeyId)).EnsureSuccessStatusCode();

        // New passphrase, same key
        var rewrapped = await org.UserA.Client.PutAsJsonAsync("/api/chat/keys/me/backup", new UpdateChatKeyBackupRequest(B64(166), B64(16), 700_000));
        (await rewrapped.Content.ReadFromJsonAsync<ChatKeyBundleDto>())!.KeyId.Should().Be(aKey.KeyId);

        // Forgotten passphrase: new key, history sent to the old key can't be unwrapped any more
        var reset = await org.UserA.Client.PostAsJsonAsync("/api/chat/keys/reset", NewKey());
        var newKey = (await reset.Content.ReadFromJsonAsync<ChatKeyBundleDto>())!;
        newKey.KeyId.Should().NotBe(aKey.KeyId);
        var msgs = (await org.UserA.Client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/chat/conversations/{conv.Id}/messages"))!;
        msgs.Single().WrappedKey.Should().BeNull();
        // ...and new messages must use the new key
        (await Send(org.UserA, conv.Id, aKey.KeyId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(org.UserA, conv.Id, newKey.KeyId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Users_without_the_messages_page_are_blocked()
    {
        var org = await NewOrg();
        var pageId = (await org.Owner.Client.GetFromJsonAsync<List<PageDto>>("/api/master/pages"))!.Single(p => p.Key == "messages").Id;
        var res = await org.Owner.Client.PutAsJsonAsync($"/api/master/users/{org.UserA.Id}/rights",
            new SetRightsRequest([new SetRightsItem(pageId, false, new PageRightsDto(false, true, false, false, false, false))]));
        res.EnsureSuccessStatusCode();
        (await org.UserA.Client.GetAsync("/api/chat/conversations")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_event_stream_notifies_members_of_new_messages_without_content()
    {
        var org = await NewOrg();
        var aKey = await SetUpKey(org.UserA);
        await SetUpKey(org.UserB);
        var conv = await Open(org.UserA, null, org.UserB);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stream = await org.UserB.Client.GetAsync("/api/chat/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        stream.StatusCode.Should().Be(HttpStatusCode.OK);
        stream.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(cts.Token));

        async Task<string> NextEvent(string name)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
                if (line != $"event: {name}") continue;
                return (await reader.ReadLineAsync(cts.Token))!;
            }
        }

        await NextEvent("ready");
        (await Send(org.UserA, conv.Id, aKey.KeyId)).EnsureSuccessStatusCode();
        var data = await NextEvent("chat");
        data.Should().Contain("\"type\":\"message\"").And.Contain(conv.Id.ToString());
        data.Should().NotContain("ciphertext");
    }
}
