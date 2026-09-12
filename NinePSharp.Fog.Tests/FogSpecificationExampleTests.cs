using Xunit;

namespace NinePSharp.Fog.Tests;

public sealed class FogSpecificationExampleTests
{
    [Theory]
    [InlineData("aan-claim", "fogaan-v1", "profile,action,server,server_boot,authdom,owner_kind,owner,key_id,logical_aname,session,base_generation,carrier,nonce,server_cert_sha256,policy_epoch,ttl_ms")]
    [InlineData("membership-read", "fogmemberop-v1", "op,service_id,cluster_id,control_boot,try_init,ip,port,generation,table_version,table_etag,row_etag,before_ticks")]
    [InlineData("storage-put", "fogstoreop-v1", "op,store_id,service_id,cluster_id,provider,state_name,grain_type,grain_key,expected,codec,payload_bytes,payload_sha256")]
    [InlineData("worker-renew", "fogworkop-v1", "op,control_boot,policy_epoch,node,worker_boot,job,scope,lease_seq,reason,result_bytes,result_sha256")]
    [InlineData("launch", "foglaunch-v1", "job,scope,node,worker_boot,control_boot,policy_epoch,bundle_sha256,profile,deadline_ns,lease_ns,memory_bytes,output_bytes,artifacts_sha256")]
    [InlineData("runtime-lock", "fogruntime-lock-v1", "runtime,provider_version,profile,engine_version,source_revision,bundle_sha256,abi,meter,platform,syscalls_sha256")]
    public void ProposedCanonicalExamplesUseTheActualProductionCodec(string file, string name, string orderedColumns)
    {
        string[] columns = orderedColumns.Split(',');
        var schema = new FogRecordSchema(name, columns, [columns[0]], [columns[0]]);
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "examples", file + ".tab"));
        var rows = schema.Parse(bytes, 4096, 1);
        Assert.Single(rows);
        Assert.Equal(bytes, schema.Serialize(rows, 4096, 1));
    }
}
