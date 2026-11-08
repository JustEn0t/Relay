using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Relay;
class FakeDeletionRemote : IDeletionRemote {
    public int Calls, Failures;
    public bool Definite;
    public PendingDeletion Last;
    public void DeletePost(PendingDeletion item) {
        Calls++; Last=item;
        if (Failures-- > 0) throw new ServiceError("Temporary delete failure",Definite);
    }
}
class EndFailureRemote : IRemote {
    public int Edits;
    public LiveInfo GetLive(){return null;}
    public int Send(string text,bool live){throw new Exception("Unexpected announcement");}
    public void Edit(int id,string text,bool live){Edits++;throw new ServiceError("Temporary final edit failure",false);}
}
class RelayDeletionChecks {
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static PendingDeletion Item(DateTime created,DateTime due,int id){return new PendingDeletion {ChatId="-100111111",BotToken="123456:FAKE_OLD",MessageId=id,CreatedAt=created,DeleteAt=due};}
    static int Main(){try{Run();Console.WriteLine("PASS: one-hour deadline, no early delete, restart persistence, no duplicate deletion, retries/backoff, channel and bot isolation, token rotation, expired permission errors, interrupted save, final-edit failure checkpoint.");return 0;}catch(Exception error){Console.WriteLine("FAIL: "+error.GetBaseException().Message);return 1;}}
    static void Run(){
        DateTime now = new DateTime(2026,11,8,18,0,0,DateTimeKind.Utc);
        var json=new JavaScriptSerializer();var data=new SavedData();data.PendingDeletions.Add(Item(now,now.AddHours(1),42));
        var remote=new FakeDeletionRemote();int saves=0;
        var queue=new DeletionQueue(data,remote,()=>saves++);
        Check(!DeletionQueue.HasDue(data,now.AddHours(1).AddTicks(-1)),"Early due flag");
        queue.Process(now.AddMinutes(59));Check(remote.Calls==0&&data.PendingDeletions.Count==1,"Post deleted before the deadline");
        data=json.Deserialize<SavedData>(json.Serialize(data));queue=new DeletionQueue(data,remote,()=>saves++);
        var result=queue.Process(now.AddHours(1));Check(result.Deleted==1&&remote.Calls==1&&data.PendingDeletions.Count==0&&saves==1,"Persisted deletion did not execute on time");
        queue.Process(now.AddHours(2));Check(remote.Calls==1,"Finished deletion repeated");
        data=new SavedData();var original=Item(now,now.AddHours(1),43);data.PendingDeletions.Add(original);
        data.Preferences.TelegramChannel="-100222222";data.Preferences.TelegramToken="654321:FAKE_OTHER";
        Check(DeletionQueue.ResolveToken(data.Preferences,original)=="123456:FAKE_OLD","Old post borrowed credentials of another bot");
        data.Preferences.TelegramToken="123456:FAKE_ROTATED";Check(DeletionQueue.ResolveToken(data.Preferences,original)=="123456:FAKE_ROTATED","Rotated token of the same bot was ignored");
        remote=new FakeDeletionRemote{Failures=1};queue=new DeletionQueue(data,remote,delegate{});
        result=queue.Process(now.AddHours(1));Check(result.Deferred==1&&data.PendingDeletions.Count==1&&original.DeleteAt==now.AddHours(1)&&original.NextAttempt==now.AddHours(1).AddSeconds(30),"Network failure lost or moved the deadline");
        queue.Process(now.AddHours(1).AddSeconds(29));Check(remote.Calls==1,"Retry backoff ignored");
        queue.Process(now.AddHours(1).AddSeconds(30));Check(remote.Calls==2&&remote.Last.ChatId=="-100111111"&&remote.Last.MessageId==43&&data.PendingDeletions.Count==0,"Retry targeted the wrong post/channel");
        data=new SavedData();var denied=Item(now,now.AddHours(1),44);data.PendingDeletions.Add(denied);
        remote=new FakeDeletionRemote{Failures=1,Definite=true};queue=new DeletionQueue(data,remote,delegate{});
        queue.Process(now.AddHours(1));Check(!denied.Failed&&denied.NextAttempt==now.AddHours(1).AddMinutes(5),"Permission retry is not recoverable");
        data=json.Deserialize<SavedData>(json.Serialize(data));queue=new DeletionQueue(data,remote,delegate{});
        queue.Process(now.AddHours(1).AddMinutes(5));Check(data.PendingDeletions.Count==0,"Restored permission retry did not recover");
        data=new SavedData();data.PendingDeletions.Add(Item(now.AddDays(-3),now.AddHours(-1),45));
        remote=new FakeDeletionRemote{Failures=1,Definite=true};queue=new DeletionQueue(data,remote,delegate{});
        result=queue.Process(now);Check(result.Failed==1&&data.PendingDeletions[0].Failed,"Expired message error was silently ignored");
        queue.Process(now.AddMinutes(10));Check(remote.Calls==1,"Permanent error loops automatically");
        string directory=Path.GetFullPath(Path.Combine("work","relay-delete-test-"+Guid.NewGuid().ToString("N")));var store=new Store(directory);
        data=new SavedData();data.PendingDeletions.Add(Item(now,now.AddHours(1),46));store.Save(data);
        remote=new FakeDeletionRemote();queue=new DeletionQueue(data,remote,()=>{throw new IOException("Simulated crash before save");});
        try{queue.Process(now.AddHours(1));}catch(IOException){}
        data=store.Load();Check(data.PendingDeletions.Count==1,"Durable checkpoint unexpectedly vanished");
        queue=new DeletionQueue(data,remote,()=>store.Save(data));queue.Process(now.AddHours(1).AddSeconds(30));
        Check(store.Load().PendingDeletions.Count==0&&remote.Calls==2,"Recovery after a lost deletion acknowledgement failed");
        var endData=new SavedData();endData.Preferences.TelegramToken="123456:FAKE_OLD";endData.Preferences.TelegramChannel="-100111111";
        endData.Stream=new StreamState{StreamId="old-stream",MessageId=47,Started=now.AddHours(-2),LastSeen=now,AnnouncedAt=now.AddHours(-2),OfflineChecks=2};
        var endRemote=new EndFailureRemote();var engine=new Engine(endData,endRemote,()=>store.Save(endData));
        try{engine.Step(now);}catch(ServiceError){}
        var restored=store.Load();Check(restored.Stream.StreamId==""&&restored.PendingDeletions.Count==1&&restored.PendingDeletions[0].DeleteAt==now.AddHours(1),"Final-card failure lost the deletion job or blocked the next stream");
        Check(restored.PendingDeletions[0].ChatId=="-100111111"&&restored.PendingDeletions[0].MessageId==47,"Finalization scheduled an unrelated message");
    }
}
