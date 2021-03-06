namespace Kafunk

open FSharp.Control
open Kafunk
open System
open System.Text

/// A producer message.
type ProducerMessage =
  struct
    /// The message payload.
    val value : Binary.Segment
    /// The optional message key.
    val key : Binary.Segment
    new (value:Binary.Segment, key:Binary.Segment) = 
      { value = value ; key = key }
  end
    with

      /// Creates a producer message.
      static member ofBytes (value:Binary.Segment, ?key) =
        ProducerMessage(value, defaultArg key Binary.empty)

      /// Creates a producer message.
      static member ofBytes (value:byte[], ?key) =
        let keyBuf = defaultArg (key |> Option.map Binary.ofArray) Binary.empty
        ProducerMessage(Binary.ofArray value, keyBuf)

      /// Creates a producer message.
      static member ofString (value:string, ?key:string) =
        let keyBuf = defaultArg (key |> Option.map (Encoding.UTF8.GetBytes >> Binary.ofArray)) Binary.empty
        ProducerMessage(Binary.ofArray (Encoding.UTF8.GetBytes value), keyBuf)

      /// Returns the size, in bytes, of the message.
      static member internal size (m:ProducerMessage) =
        m.key.Count + m.value.Count

/// A producer response.
type ProducerResult =
  struct
    /// The partition to which the message was written.
    val partition : Partition
    
    /// The offset of the first message produced to the partition.
    val offset : Offset
    
    new (p,o) = { partition = p ; offset = o }
  end

/// The number of partitions of a topic.
type PartitionCount = int

/// A partition function.
type Partitioner = TopicName * PartitionCount * ProducerMessage -> Partition

/// Partition functions.
[<Compile(Module)>]
module Partitioner =

  open System.Threading

  let private ensurePartitions (ps:PartitionCount) =
    if ps = 0 then invalidArg "ps" "must have partitions"
    
  /// Creates a partition function.
  let create (f:TopicName * PartitionCount * ProducerMessage -> Partition) : Partitioner = 
    f

  /// Computes the partition.
  let partition (p:Partitioner) (t:TopicName) (pc:PartitionCount) (m:ProducerMessage) =
    p (t,pc,m)

  /// Constantly returns the same partition.
  let konst (p:Partition) : Partitioner =
    create <| konst p

  /// Round-robin partition assignment.
  let roundRobin : Partitioner =
    let mutable i = 0
    create <| fun (_,pc,_) -> 
      ensurePartitions pc
      let i' = Interlocked.Increment &i
      if i' < 0 then
        Interlocked.Exchange (&i, 0) |> ignore
        0
      else 
        i' % pc

  /// Computes the hash-code of the routing key to get the topic partition.
  let hashKey (h:Binary.Segment -> int) : Partitioner =
    create <| fun (_,pc,pm) -> 
      ensurePartitions pc
      abs (h pm.key) % pc

  /// Random partition assignment.
  let rand (seed:int option) : Partitioner =
    let rng = 
      match seed with 
      | Some s -> new Random(s)
      | None -> new Random()
    let inline next pc = lock rng (fun () -> rng.Next (0, pc))
    create <| fun (_,pc,_) ->
      ensurePartitions pc
      next pc
  
  /// CRC32 of the message key.
  let crc32Key : Partitioner =
    hashKey (fun key -> int (Crc.crc32 key.Array key.Offset key.Count))


/// Producer configuration.
type ProducerConfig = {

  /// The topic to produce to.
  topic : TopicName

  /// A partition function which given a topic name, cluster topic metadata and the message payload, returns the partition
  /// which the message should be written to.
  partitioner : Partitioner

  /// The acks required.
  /// Default: Local
  requiredAcks : RequiredAcks

  // The amount of time, in milliseconds, the broker will wait trying to meet the RequiredAcks 
  /// requirement before sending back an error to the client.
  /// Default: 10000
  timeout : Timeout

  /// The compression method to use.
  /// Default: None
  compression : byte

  /// The per-broker, in-memory buffer size in bytes.
  /// When the buffer reaches capacity, backpressure is exerted on incoming produce requests.
  /// Default: 33554432
  bufferSizeBytes : int

  /// The maximum size, in bytes, of a batch of produce requests per broker.
  /// If set to 0, no batching will be performed.
  /// Default: 16384
  batchSizeBytes : int

  /// The maximum time, in milliseconds, to wait for a produce request btch to reach capacity.
  /// If set to 0, no batching will be performed.
  /// Default: 1
  batchLingerMs : int

  /// The retry policy for produce requests.
  /// Default: RetryPolicy.constantMs 1000 |> RetryPolicy.maxAttempts 10
  retryPolicy : RetryPolicy

} with

  /// The default required acks = RequiredAcks.Local.
  static member DefaultRequiredAcks = RequiredAcks.AllInSync

  /// The default produce request timeout = 10000.
  static member DefaultTimeoutMs = 10000

  /// The default per-broker, produce request batch size in bytes = 16384.
  static member DefaultBatchSizeBytes = 16384

  /// The default in-memory, per-broker buffer size = 33554432.
  static member DefaultBufferSizeBytes = 33554432

  /// The default per-broker, produce request linger time in ms = 1.
  static member DefaultBatchLingerMs = 1

  /// The default produce request retry policy = RetryPolicy.constantMs 1000 |> RetryPolicy.maxAttempts 10.
  static member DefaultRetryPolicy = RetryPolicy.constantMs 1000 |> RetryPolicy.maxAttempts 10

  /// Creates a producer configuration.
  static member create (topic:TopicName, partition:Partitioner, ?requiredAcks:RequiredAcks, ?compression:byte, ?timeout:Timeout, 
                        ?bufferSizeBytes:int, ?batchSizeBytes, ?batchLingerMs, ?retryPolicy) =
    {
      topic = topic
      partitioner = partition
      requiredAcks = defaultArg requiredAcks ProducerConfig.DefaultRequiredAcks
      compression = defaultArg compression CompressionCodec.None
      timeout = defaultArg timeout ProducerConfig.DefaultTimeoutMs
      bufferSizeBytes = defaultArg bufferSizeBytes ProducerConfig.DefaultBufferSizeBytes
      batchSizeBytes = defaultArg batchSizeBytes ProducerConfig.DefaultBatchSizeBytes
      batchLingerMs = defaultArg batchLingerMs ProducerConfig.DefaultBatchLingerMs
      retryPolicy = defaultArg retryPolicy ProducerConfig.DefaultRetryPolicy
    }

  static member internal messageVersion (connVersion:Version) = 
    Versions.produceReqMessage (Versions.byKey connVersion ApiKey.Produce)


/// A producer sends batches of topic and message set pairs to the appropriate Kafka brokers.
type Producer = private {
  conn : KafkaConn
  config : ProducerConfig
  messageVersion : ApiVersion
  state : Resource<ProducerState>
  produceBatch : (PartitionCount -> Partition * ProducerMessage[]) -> Async<ProducerResult>
}

/// Producer state corresponding to the state of a cluster.
and private ProducerState = {

  /// Brokers.
  brokerByEndPoint : Map<EndPoint, Chan>
  
  /// Brokers by partitions.
  partitionBrokers : Map<Partition, Chan>
  
  /// Current set of partitions for the topic.
  partitions : Partition[]
  
  /// Per-broker queues accepting a triple of partition, messages for the partition and a reply channel.
  brokerQueues : Map<EndPoint, ProducerMessageBatch -> Async<unit>>
}

and private ProducerMessageBatch =
  struct
    val partition : Partition
    val messages : ProducerMessage[]
    val rep : IVar<Result<ProducerResult, ChanError list>>
    val size : int
    new (p,ms,rep,size) = { partition = p ; messages = ms ; rep = rep ; size = size }
  end


/// High-level producer API.
[<Compile(Module)>]
module Producer =

  open System.Threading
  open System.Threading.Tasks

  let private Log = Log.create "Kafunk.Producer"

  let private messageBatchSizeBytes (batch:ProducerMessage seq) =
    let mutable size = 0
    for m in batch do
      size <- size + (ProducerMessage.size m)
    size

  let private batchSizeBytes (batch:ProducerMessageBatch seq) =
    let mutable size = 0
    for b in batch do
      size <- size + b.size
    size

  /// Sends a batch of messages to a broker, and replies to the respective reply channels.
  let private sendBatch 
    (cfg:ProducerConfig) 
    (messageVer:int16) 
    (ch:Chan) 
    (batch:ProducerMessageBatch seq) = async {
    //Log.info "sending_batch|ep=%O batch_size_count=%i batch_size_bytes=%i" (Chan.endpoint ch) batch.Length (batchSizeBytes batch)

    let pms = 
      batch
      |> Seq.groupBy (fun b -> b.partition)
      |> Seq.map (fun (p,pms) ->
        let ms = 
          pms 
          |> Seq.collect (fun bs -> bs.messages |> Seq.map (fun pm -> Message.create pm.value pm.key None))
          |> MessageSet.ofMessages
          |> Compression.compress messageVer cfg.compression
        p,ms)
      |> Seq.toArray

    let req = ProduceRequest(cfg.requiredAcks, cfg.timeout, [| (cfg.topic, pms |> Array.map (fun (p,ms) -> (p, MessageSet.size ms, ms))) |])
    let! res = Chan.send ch (RequestMessage.Produce req) |> Async.map (Result.map ResponseMessage.toProduce)
    match res with
    | Success res ->
      
      let oks,retryErrors,fatalErrors =
        res.topics
        |> Seq.collect (fun (_t,os) ->
          os
          |> Seq.map (fun (p,ec,o) ->
            match ec with
            | ErrorCode.NoError -> Choice1Of3 (p,o)

            // timeout  (retry)
            | ErrorCode.RequestTimedOut | ErrorCode.LeaderNotAvailable | ErrorCode.NotEnoughReplicasCode
            | ErrorCode.NotEnoughReplicasAfterAppendCode -> Choice2Of3 (p,o,ec)

            // topology change (retry)
            | ErrorCode.UnknownTopicOrPartition | ErrorCode.NotLeaderForPartition -> Choice2Of3 (p,o,ec)

            // 404
            | ErrorCode.InvalidMessage | ErrorCode.MessageSizeTooLarge
            | ErrorCode.InvalidTopicCode | ErrorCode.InvalidRequiredAcksCode -> Choice3Of3 (p,o,ec)
            
            // other
            | _ -> Choice3Of3 (p,o,ec)))
        |> Seq.partitionChoices3
      
      // TODO: error only by partition
      if fatalErrors.Length > 0 then
        let msg = sprintf "produce_fatal_errors|ep=%O errors=%A request=%s response=%s" (Chan.endpoint ch) fatalErrors (ProduceRequest.Print req) (ProduceResponse.Print res) 
        Log.error "%s" msg
        let ex = exn(msg)
        let eps = fatalErrors |> Seq.map (fun (p,_,_) -> p,ex) |> Map.ofSeq
        for b in batch do
          Map.tryFind b.partition eps
          |> Option.iter (fun ex -> IVar.error ex b.rep)
      
      if retryErrors.Length > 0 then
        Log.error "produce_transient_errors|ep=%O errors=%A request=%s response=%s" 
          (Chan.endpoint ch) retryErrors (ProduceRequest.Print req) (ProduceResponse.Print res) 
        let res = Failure [] // TODO: specific error
        let eps = retryErrors |> Seq.map (fun (p,_,_) -> p,res) |> Map.ofSeq
        for b in batch do
          Map.tryFind b.partition eps
          |> Option.iter (fun res -> IVar.put res b.rep)

      if oks.Length > 0 then
        let oks = oks |> Seq.map (fun (p,o) -> p, Success (ProducerResult(p,o))) |> Map.ofSeq
        for b in batch do
          Map.tryFind b.partition oks 
          |> Option.iter (fun res -> IVar.put res b.rep)
          
    | Failure err ->
      let err = Failure err
      for b in batch do
        IVar.put err b.rep 
      return () }

  /// Fetches cluster state and initializes a per-broker produce buffer.
  let private getState (conn:KafkaConn) (cfg:ProducerConfig) (ct:CancellationToken) (prevState:ProducerState option) = async {
    
    Log.info "fetching_topic_metadata|topic=%s" cfg.topic
    let! state = conn.GetMetadataState [|cfg.topic|]

    let partitions = 
      Routes.topicPartitions state.routes |> Map.find cfg.topic
    
    Log.info "establishing_broker_connections|topic=%s partitions=[%s]" cfg.topic (Printers.partitions partitions)

    let brokerByEndPoint =
      prevState
      |> Option.map (fun s -> s.brokerByEndPoint)
      |> Option.getOr Map.empty

    let! brokerByEndPoint,brokerByPartition =
      ((brokerByEndPoint, Map.empty), AsyncSeq.ofSeq partitions)
      ||> AsyncSeq.foldAsync (fun (brokerByEndPoint,brokerByPartition) p -> async {
        match Routes.tryFindHostForTopic state.routes (cfg.topic,p) with
        | Some ep -> 
          match ConnState.tryFindChanByEndPoint ep state with
          | Some ch ->
            return (Map.add ep ch brokerByEndPoint, Map.add p ch brokerByPartition)
          | None -> 
            match brokerByEndPoint |> Map.tryFind ep with
            | Some ch ->
              return (brokerByEndPoint, Map.add p ch brokerByPartition)
            | None ->
              let! ch = Chan.connect (conn.Config.version,conn.Config.tcpConfig,conn.Config.clientId) ep
              return (Map.add ep ch brokerByEndPoint, Map.add p ch brokerByPartition)
        | None -> 
          return failwith "invalid state" })
    
    let messageVer = ProducerConfig.messageVersion conn.Config.version

    let! ct' = Async.CancellationToken
    let queueCts = CancellationTokenSource.CreateLinkedTokenSource (ct, ct')

    let batchSizeCondition (buf:ResizeArray<ProducerMessageBatch>) =
      (batchSizeBytes buf) >= cfg.batchSizeBytes

    let brokerQueue (ch:Chan) =
      let sendBatch = sendBatch cfg messageVer ch
      let buf = BoundedMb.createByCondition (fun buf -> batchSizeBytes buf >= cfg.bufferSizeBytes)
      let produceStream = 
        AsyncSeq.replicateInfiniteAsync (BoundedMb.take buf)
      let sendProcess =
        if cfg.batchSizeBytes = 0 || cfg.batchLingerMs = 0 then
          produceStream
          |> AsyncSeq.iterAsync (Array.singleton >> sendBatch)
        else
          produceStream
          |> AsyncSeq.bufferByConditionAndTime batchSizeCondition cfg.batchLingerMs
          |> AsyncSeq.iterAsync sendBatch
      sendProcess
      |> Async.tryWith (fun ex -> async {
        Log.error "producer_broker_queue_exception|ep=%O error=%O" (Chan.endpoint ch) ex })
      |> (fun x -> Async.Start (x, queueCts.Token))
      (flip BoundedMb.put buf)
   
    let brokerQueues =
      brokerByPartition
      |> Map.toSeq
      |> Seq.map (fun (_,ch) ->
        let ep = Chan.endpoint ch
        let q = brokerQueue ch
        ep,q)
      |> Map.ofSeq
        
    return { partitions = partitions ; partitionBrokers = brokerByPartition ; brokerQueues = brokerQueues ; brokerByEndPoint = brokerByEndPoint } }

  let private produceBatchInternal (state:ProducerState) (createBatch:PartitionCount -> Partition * ProducerMessage[]) = async {
    let p,ms = createBatch state.partitions.Length
    let rep = IVar.create ()
    let batch = ProducerMessageBatch(p,ms,rep,messageBatchSizeBytes ms)
    let q = 
      let ep = state.partitionBrokers |> Map.find p |> Chan.endpoint
      state.brokerQueues |> Map.find ep
    do! q batch
    let! res = IVar.get rep
    match res with
    | Success res ->
      return Success res
    | Failure errs ->
      return Failure (Resource.ResourceErrorAction.RecoverRetry (exn(sprintf "%A" errs))) }

  /// Creates a producer.
  let createAsync (conn:KafkaConn) (config:ProducerConfig) : Async<Producer> = async {
    Log.info "initializing_producer|topic=%s" config.topic
    let messageVersion = Versions.produceReqMessage (Versions.byKey conn.Config.version ApiKey.Produce)
    let! resource = 
      Resource.recoverableRecreate 
        (getState conn config) 
        (fun (s,v,_req,ex) -> async {
          Log.warn "closing_resource|version=%i topic=%s partitions=[%s] error=%O" v config.topic (Printers.partitions s.partitions) ex
          return () })
    let! state = Resource.get resource
    let produceBatch = Resource.injectWithRecovery resource config.retryPolicy (produceBatchInternal)
    let p = { state = resource ; config = config ; conn = conn ; messageVersion = messageVersion ; produceBatch = produceBatch }
    Log.info "producer_initialized|topic=%s partitions=[%s]" config.topic (Printers.partitions state.partitions)
    return p }

  /// Creates a producer.
  let create (conn:KafkaConn) (cfg:ProducerConfig) : Producer =
    createAsync conn cfg |> Async.RunSynchronously

  /// Produces a message. 
  /// The message will be sent as part of a batch and the result will correspond to the offset
  /// produced by the entire batch.
  let produce (p:Producer) (m:ProducerMessage) =
    p.produceBatch (fun ps -> Partitioner.partition p.config.partitioner p.config.topic ps m, [|m|])

  /// Produces a batch of messages using the specified function which creates messages given a set of partitions
  /// currently configured for the topic.
  /// The messages will be sent as part of a batch and the result will correspond to the offset
  /// produced by the entire batch.
  let produceBatch (p:Producer) =
    p.produceBatch