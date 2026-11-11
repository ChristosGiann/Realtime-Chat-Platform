using System.Net;
using System.Net.Http.Json;
using Chat.Api.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private static string DeliveryUrl(Guid conversation) => $"/api/conversations/{conversation}/delivery-state";
    private static Task<DeliveryStateResponse?> Delivery(HttpClient client, Guid conversation) => client.GetFromJsonAsync<DeliveryStateResponse>(DeliveryUrl(conversation));
    private static Task<HttpResponseMessage> Deliver(HttpClient client, Guid conversation, Guid message, Guid membership) =>
        client.PutAsJsonAsync(DeliveryUrl(conversation), new { messageId = message, membershipId = membership });

    [Fact]
    public async Task DeliveryIsIndependentMonotonicAndReadAtomicallyRaisesItsFloor()
    {
        using var alice = Client(); using var bob = Client(); await Login(alice); var b = await Login(bob);
        var conversation = await Group(alice, b); var membership = (await Delivery(bob, conversation))!.MembershipId;
        var first = (await (await Send(alice, conversation, "first")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var second = (await (await Send(alice, conversation, "second")).Content.ReadFromJsonAsync<MessageResponse>())!;
        Assert.Null((await Delivery(bob, conversation))!.LastDeliveredMessageId);
        Assert.Equal(HttpStatusCode.OK, (await Deliver(bob, conversation, first.Id, membership)).StatusCode);
        Assert.Null((await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.LastReadMessageId);
        var attempts = Enumerable.Range(0, 12).Select(i => i % 3 == 0 ? Advance(bob, conversation, second.Id)
            : Deliver(bob, conversation, i % 2 == 0 ? first.Id : second.Id, membership));
        Assert.All(await Task.WhenAll(attempts), response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var state = (await Delivery(bob, conversation))!;
        Assert.Equal(second.Id, state.LastDeliveredMessageId); Assert.Equal(second.CreatedAt, state.LastDeliveredAt);
        Assert.Equal(second.Id, (await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.LastReadMessageId);
        Assert.Null((await Delivery(alice, conversation))!.LastDeliveredMessageId);
        await Deliver(bob, conversation, first.Id, membership); Assert.Equal(state, await Delivery(bob, conversation));
    }

    [Fact]
    public async Task DeliveryRejoinChangesIncarnationAndRejectsOldAcknowledgments()
    {
        using var owner = Client(); using var member = Client(); await Login(owner); var b = await Login(member);
        var conversation = await Group(owner, b); var old = (await Delivery(member, conversation))!.MembershipId;
        var message = (await (await Send(owner, conversation, "Retained history")).Content.ReadFromJsonAsync<MessageResponse>())!;
        await Deliver(member, conversation, message.Id, old); await Advance(member, conversation, message.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await Leave(member, conversation)).StatusCode);
        await Problem(await member.GetAsync(DeliveryUrl(conversation)), HttpStatusCode.NotFound);
        await Add(owner, conversation, b);
        var fresh = (await Delivery(member, conversation))!;
        Assert.NotEqual(old, fresh.MembershipId); Assert.Null(fresh.LastDeliveredMessageId);
        Assert.Null((await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.LastReadMessageId);
        await Problem(await Deliver(member, conversation, message.Id, old), HttpStatusCode.Conflict);
        await Problem(await member.PutAsJsonAsync(ReadUrl(conversation), new { messageId = message.Id, membershipId = old }), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.OK, (await member.PutAsJsonAsync(ReadUrl(conversation), new { messageId = message.Id, membershipId = fresh.MembershipId })).StatusCode);
        Assert.Equal(message.Id, (await Delivery(member, conversation))!.LastDeliveredMessageId);
    }

    [Fact]
    public async Task DeliveryRejectsNonMembersSpoofingForeignMessagesAndMissingCsrf()
    {
        using var owner = Client(); using var other = Client(); using var anonymous = Client(); await Login(owner); await Login(other);
        var conversation = await Group(owner); var foreign = await Group(owner); var membership = (await Delivery(owner, conversation))!.MembershipId;
        var message = (await (await Send(owner, foreign, "foreign")).Content.ReadFromJsonAsync<MessageResponse>())!;
        await Problem(await Deliver(owner, conversation, message.Id, membership), HttpStatusCode.NotFound);
        await Problem(await Deliver(owner, conversation, Guid.Empty, membership), HttpStatusCode.BadRequest);
        await Problem(await owner.PutAsJsonAsync(DeliveryUrl(conversation), new { messageId = message.Id }), HttpStatusCode.BadRequest);
        await Problem(await owner.PutAsJsonAsync(DeliveryUrl(conversation), new { messageId = message.Id, membershipId = membership, userId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        await Problem(await other.GetAsync(DeliveryUrl(conversation)), HttpStatusCode.NotFound);
        await Problem(await Deliver(other, conversation, message.Id, membership), HttpStatusCode.NotFound);
        await Problem(await anonymous.GetAsync(DeliveryUrl(conversation)), HttpStatusCode.Unauthorized);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Problem(await Deliver(owner, conversation, message.Id, membership), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeliveryUsesPostgresTupleOrderAndDatabaseEnforcesForeignKey()
    {
        using var client = Client(); var user = await Login(client); var conversation = await Group(client); var other = await Group(client);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Messages.AddRange(Enumerable.Range(0, 3).Select(_ => new Message { ConversationId = conversation, SenderId = user, CreatedAt = timestamp, Body = "tie" }));
        await db.SaveChangesAsync();
        var ids = await db.Messages.Where(x => x.ConversationId == conversation).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        var membership = (await Delivery(client, conversation))!.MembershipId;
        await Deliver(client, conversation, ids[2], membership); await Deliver(client, conversation, ids[0], membership);
        Assert.Equal(ids[2], (await Delivery(client, conversation))!.LastDeliveredMessageId);
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE conversations.\"ConversationMembers\" SET \"LastDeliveredMessageId\" = {ids[0]} WHERE \"ConversationId\" = {other}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM messages.\"Messages\" WHERE \"Id\" = {ids[2]}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, deletion.SqlState);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE conversations.\"ConversationMembers\" SET \"MembershipId\" = {membership} WHERE \"ConversationId\" = {other}"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var empty = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE conversations.\"ConversationMembers\" SET \"MembershipId\" = {Guid.Empty} WHERE \"ConversationId\" = {other}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, empty.SqlState);
    }

    [Fact]
    public async Task DeliveryRepairsLegacyReadFloorWithoutLoweringReadOrDelivery()
    {
        using var client = Client(); await Login(client); var conversation = await Group(client);
        var first = (await (await Send(client, conversation, "first")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var second = (await (await Send(client, conversation, "second")).Content.ReadFromJsonAsync<MessageResponse>())!;
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Database.ExecuteSqlInterpolatedAsync($"UPDATE conversations.\"ConversationMembers\" SET \"LastReadMessageId\" = {second.Id} WHERE \"ConversationId\" = {conversation}");
        var state = (await Delivery(client, conversation))!;
        await Deliver(client, conversation, first.Id, state.MembershipId);
        Assert.Equal(second.Id, (await Delivery(client, conversation))!.LastDeliveredMessageId);
        Assert.Equal(second.Id, (await client.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.LastReadMessageId);
    }

    [Fact]
    public async Task DeliveryAndReadUpdatesRollBackTogether()
    {
        using var client = Client(); var user = await Login(client); var conversation = await Group(client);
        var message = (await (await Send(client, conversation, "rollback")).Content.ReadFromJsonAsync<MessageResponse>())!;
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleAsync();
            Assert.True(await ReceiptProgress.Advance(db, conversation, user, message.Id, true, CancellationToken.None));
            await transaction.RollbackAsync();
        }
        Assert.Null((await Delivery(client, conversation))!.LastDeliveredMessageId);
        Assert.Null((await client.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.LastReadMessageId);
    }
}
