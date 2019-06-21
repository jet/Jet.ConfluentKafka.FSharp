module Jet.ConfluentKafka.FSharp.Monitor

open Confluent.Kafka
open Serilog
open System
open System.Threading

[<AutoOpen>]
module Shims =
    module Option =
        let someOr = Option.orElse
        let getValueOr = Option.defaultValue        
    module Strings =
        let join = String.concat
    let mapSnd f (x,y)= x,f y

type Async =
  static member inline bind (f:'a -> Async<'b>) (a:Async<'a>) : Async<'b> = async.Bind(a, f)
  static member map f xAsync = async {
            // get the contents of xAsync 
            let! x = xAsync 
            // apply the function and lift the result
            return f x
            }

  /// Like Async.StartWithContinuations but starts the computation on a ThreadPool thread.
  static member StartThreadPoolWithContinuations (a:Async<'a>, ok:'a -> unit, err:exn -> unit, cnc:OperationCanceledException -> unit, ?ct:CancellationToken) =
    let a = Async.SwitchToThreadPool () |> Async.bind (fun _ -> a)
    Async.StartWithContinuations (a, ok, err, cnc, defaultArg ct CancellationToken.None)
    
  /// Creates an async computation which completes when any of the argument computations completes.
  /// The other argument computation is cancelled.
  static member choose (a:Async<'a>) (b:Async<'a>) : Async<'a> =
    Async.FromContinuations <| fun (ok,err,cnc) ->
      let state = ref 0
      let cts = new CancellationTokenSource()
      let inline cancel () =
        cts.Cancel()
        cts.Dispose()
      let inline ok a =
        if (Interlocked.CompareExchange(state, 1, 0) = 0) then
          cancel ()
          ok a
      let inline err (ex:exn) =
        if (Interlocked.CompareExchange(state, 1, 0) = 0) then
          cancel ()
          err ex
      let inline cnc ex =
        if (Interlocked.CompareExchange(state, 1, 0) = 0) then
          cancel ()
          cnc ex
      Async.StartThreadPoolWithContinuations (a, ok, err, cnc, cts.Token)
      Async.StartThreadPoolWithContinuations (b, ok, err, cnc, cts.Token)
      
type KafkaConsumer = internal {
            confluentConsumer : Confluent.Kafka.IConsumer<obj,obj>
            config : KafkaConsumerConfig
        } with
            member this.ConfluentConsumer = this.confluentConsumer
            member this.Config = this.config

module Legacy =

     
  module Map =

    let mergeChoice (f:'a -> Choice<'b * 'c, 'b, 'c> -> 'd) (map1:Map<'a, 'b>) (map2:Map<'a, 'c>) : Map<'a, 'd> =
      Set.union (map1 |> Seq.map (fun k -> k.Key) |> set) (map2 |> Seq.map (fun k -> k.Key) |> set)
      |> Seq.map (fun k ->
        match Map.tryFind k map1, Map.tryFind k map2 with
        | Some b, Some c -> k, f k (Choice1Of3 (b,c))
        | Some b, None   -> k, f k (Choice2Of3 b)
        | None,   Some c -> k, f k (Choice3Of3 c)
        | None,   None   -> failwith "invalid state")
      |> Map.ofSeq
  
  /// Progress information for a consumer in a group.
  type ConsumerProgressInfo = {

    /// The consumer group id.
    group : string

    /// The name of the kafka topic.
    topic : string

    /// Progress info for each partition.
    partitions : ConsumerPartitionProgressInfo[]

    /// The total lag across all partitions.
    totalLag : int64

    /// The minimum lead across all partitions.
    minLead : int64

  }

  /// Progress information for a consumer in a group, for a specific topic-partition.
  and ConsumerPartitionProgressInfo = {

    /// The partition id within the topic.
    partition : int

    /// The consumer's current offset.
    consumerOffset : Offset

    /// The offset at the current start of the topic.
    earliestOffset : Offset

    /// The offset at the current end of the topic.
    highWatermarkOffset : Offset

    /// The distance between the high watermark offset and the consumer offset.
    lag : int64

    /// The distance between the consumer offset and the earliest offset.
    lead : int64

    /// The number of messages in the partition.
    messageCount : int64

  }

 /// Operations for providing consumer progress information.
  module ConsumerInfo =
    /// Returns consumer progress information.
    /// Passing empty set of partitions returns information for all partitions.
    /// Note that this does not join the group as a consumer instance
    let progress (admin : IAdminClient, consumer:IConsumer<_,_>) (topic:string) (ps:int[]) = async {
      let! topicPartitions =
        if ps |> Array.isEmpty then
          async {
            let meta =
              admin
                  .GetMetadata((*false,*) TimeSpan.FromSeconds(40.0)).Topics
              |> Seq.find(fun t -> t.Topic = topic)

            return
              meta.Partitions
              |> Seq.map(fun p -> new TopicPartition(topic, new Partition(p.PartitionId)))
               }
        else
          async { return ps |> Seq.map(fun p -> new TopicPartition(topic,new Partition(p))) }

      let committedOffsets =
        consumer.Committed(topicPartitions, TimeSpan.FromSeconds(20.))
        |> Seq.sortBy(fun e -> e.Partition.Value)
        |> Seq.map(fun e -> e.Partition.Value, e)
        |> Map.ofSeq

      let! watermarkOffsets =
        topicPartitions
          |> Seq.map(fun tp -> async {
            return tp.Partition.Value, consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds 40.)}
          )
          |> Async.Parallel

      let watermarkOffsets =
        watermarkOffsets
        |> Map.ofArray

      let partitions =
        (watermarkOffsets, committedOffsets)
        ||> Map.mergeChoice (fun p -> function
          | Choice1Of3 (hwo,cOffset) ->
            let e,l,o = hwo.Low.Value,hwo.High.Value,cOffset.Offset.Value
            // Consumer offset of (Invalid Offset -1001) indicates that no consumer offset is present.  In this case, we should calculate lag as the high water mark minus earliest offset
            let lag, lead =
              match o with
              | offset when offset = Offset.Unset.Value -> l - e, 0L
              | _ -> l - o, o - e
            { partition = p ; consumerOffset = cOffset.Offset ; earliestOffset = hwo.Low ; highWatermarkOffset = hwo.High ; lag = lag ; lead = lead ; messageCount = l - e }
          | Choice2Of3 hwo ->
            // in the event there is no consumer offset present, lag should be calculated as high watermark minus earliest
            // this prevents artifically high lags for partitions with no consumer offsets
            let e,l = hwo.Low.Value,hwo.High.Value
            { partition = p ; consumerOffset = Offset.Unset; earliestOffset = hwo.Low ; highWatermarkOffset = hwo.High ; lag = l - e ; lead = 0L ; messageCount = l - e }
            //failwithf "unable to find consumer offset for topic=%s partition=%i" topic p
          | Choice3Of3 o ->
            let invalid = Offset.Unset
            { partition = p ; consumerOffset = o.Offset ; earliestOffset = invalid ; highWatermarkOffset = invalid ; lag = invalid.Value ; lead = invalid.Value ; messageCount = -1L })
        |> Seq.map (fun kvp -> kvp.Value)
        |> Seq.toArray

      return
        {
        topic = topic ; group = consumer.Name ; partitions = partitions ;
        totalLag = partitions |> Seq.sumBy (fun p -> p.lag)
        minLead =
          if partitions.Length > 0 then
            partitions |> Seq.map (fun p -> p.lead) |> Seq.min
          else Offset.Unset.Value }}
    
   type PartitionResultKey =
    | NoErrorKey
    | Rule2ErrorKey
    | Rule3ErrorKey

type PartitionResult =
    | NoError
    | Rule2Error of int64
    | Rule3Error
with
    static member toKey = function
        | NoError -> Legacy.NoErrorKey
        | Rule2Error _ -> Legacy.Rule2ErrorKey
        | Rule3Error -> Legacy.Rule3ErrorKey
    static member toString = function
        | NoError ->  ""
        | Rule2Error lag -> string lag
        | Rule3Error -> ""

// rdkafka uses -1001 to represent an invalid offset (usually means the consumer has not committed offsets for that partition)
let [<Literal>] rdkafkaInvalidOffset = -1001L

type OffsetValue =
    | Missing
    | Valid of int64
with
    static member ofOffset(offset : Offset) =
        match offset.Value with
        | value when value = rdkafkaInvalidOffset -> Missing
        | valid -> Valid valid
    override this.ToString() =
        match this with
        | Missing -> "Missing"
        | Valid value -> value.ToString()

type PartitionInfo = {
    partition : int
    consumerOffset : OffsetValue
    earliestOffset : OffsetValue
    highWatermarkOffset : OffsetValue
    lag : int64
} with
    static member ofConsumerPartitionProgressInfo(info : Legacy.ConsumerPartitionProgressInfo) = {
        partition = info.partition
        consumerOffset = OffsetValue.ofOffset info.consumerOffset
        earliestOffset = OffsetValue.ofOffset info.earliestOffset
        highWatermarkOffset = OffsetValue.ofOffset info.highWatermarkOffset
        lag = info.lag
    }

type Window = Window of PartitionInfo []

let createPartitionInfoList (info : Legacy.ConsumerProgressInfo) =
    info.partitions
    |> Array.map PartitionInfo.ofConsumerPartitionProgressInfo
    |> Window

// Naive insert and copy out buffer
type private RingBuffer<'A> (capacity : int) =
    let lockObj = obj()
    let mutable head = 0
    let mutable tail = -1
    let mutable size = 0
    let buffer : 'A [] = Array.zeroCreate capacity

    let copy () =
        let arr = Array.zeroCreate size
        let mutable i = head
        for x = 0 to size - 1 do
            arr.[x] <- buffer.[i % capacity]
            i <- i + 1
        arr

    let add (x : 'A) =
        tail <- (tail + 1) % capacity
        buffer.[tail] <- x
        if (size < capacity) then
            size <- size + 1
        else
            head <- (head + 1) % capacity

    member __.SafeFullClone () =
        lock lockObj (fun () -> if size = capacity then copy() else [||])

    member __.SafeAdd (x : 'A) =
        lock lockObj (fun _ -> add x)

    member __.Reset () =
        lock lockObj (fun _ ->
            head <- 0
            tail <- -1
            size <- 0)

module Rules =

    // Rules taken from https://github.com/linkedin/Burrow
    // Rule 1:  If over the stored period, the lag is ever zero for the partition, the period is OK
    // Rule 2:  If the consumer offset does not change, and the lag is non-zero, it's an error (partition is stalled)
    // Rule 3:  If the consumer offsets are moving, but the lag is consistently increasing, it's a warning (consumer is slow)

    // The following rules are not implementable given our poll based implementation - they should also not be needed
    // Rule 4:  If the difference between now and the lastPartition offset timestamp is greater than the difference between the lastPartition and firstPartition offset timestamps, the
    //          consumer has stopped committing offsets for that partition (error), unless
    // Rule 5:  If the lag is -1, this is a special value that means there is no broker offset yet. Consider it good (will get caught in the next refresh of topics)

    // If lag is ever zero in the window
    let checkRule1 (partitionInfoWindow : PartitionInfo []) =
        if partitionInfoWindow |> Array.exists (fun i -> i.lag = 0L)
        then Some NoError
        else None

    // If there is lag, the offsets should be progressing in window
    let checkRule2 (partitionInfoWindow : PartitionInfo []) =
        let offsetsIndicateLag (firstConsumerOffset : OffsetValue) (lastConsumerOffset : OffsetValue) =
            match (firstConsumerOffset, lastConsumerOffset) with
            | Valid validFirst, Valid validLast ->
                validLast - validFirst <= 0L
            | Missing, Valid _ ->
                // Partition got its initial offset value this window, check again next window.
                false
            | Valid _, Missing ->
                // Partition somehow lost its offset in this window, something's probably wrong.
                true
            | Missing, Missing ->
                // Partition has invalid offsets for the entire window, there may be lag.
                true

        let firstWindowPartitions = partitionInfoWindow |> Array.head
        let lastWindowPartitions = partitionInfoWindow |> Array.last

        let checkPartitionForLag (firstWindowPartition : PartitionInfo) (lastWindowPartition : PartitionInfo)  =
            match lastWindowPartition.lag with
            | 0L -> None
            | lastPartitionLag when offsetsIndicateLag firstWindowPartition.consumerOffset lastWindowPartition.consumerOffset ->
                if lastWindowPartition.partition <> firstWindowPartition.partition then failwithf "Partitions did not match in rule2"
                Some (Rule2Error lastPartitionLag)
            | _ -> None

        checkPartitionForLag firstWindowPartitions lastWindowPartitions

    // Has the lag reduced between steps in the window
    let checkRule3 (partitionInfoWindow : PartitionInfo []) =
        let lagDecreasing =
            partitionInfoWindow
            |> Seq.pairwise
            |> Seq.exists (fun (prev, curr) -> curr.lag < prev.lag)

        if lagDecreasing
        then None
        else Some Rule3Error

    let checkRulesForPartition (partitionInfoWindow : PartitionInfo []) =
        checkRule1 partitionInfoWindow
        |> Option.someOr (checkRule2 partitionInfoWindow)
        |> Option.someOr (checkRule3 partitionInfoWindow)
        |> Option.getValueOr NoError

    let checkRulesForAllPartitions (windows : Window []) =
        windows
        |> Array.collect (fun (Window partitionInfo) -> partitionInfo)
        |> Array.groupBy (fun p -> p.partition)
        |> Array.map (fun (p, info) -> (p, checkRulesForPartition info))

module private Logging =

    let private formatRule2Errors consumerGroup topic errors =
        let stalledPartitions =
            errors
            |> Seq.map (fun (partition, lag) -> sprintf "(%i, %s)" partition lag)
            |> Strings.join " "

        sprintf "Lag present and offsets not progressing (partition,lag)|consumerGroup=%s|topic=%s|stalledPartitions=%s" consumerGroup topic stalledPartitions

    let private formatRule3Errors consumerGroup topic errors =
        let laggingPartitions =
            errors
            |> Seq.map (fun (partition, lag) -> sprintf "(%i, %s)" partition lag)
            |> Strings.join " "

        sprintf "Consumer lag is consistently increasing|consumerGroup=%s|topic=%s|laggingPartitions=%s" consumerGroup topic laggingPartitions

    let logResults (log : ILogger) consumerGroup topic (partitionResults : (int * PartitionResult) []) =

        let results = partitionResults |> Array.groupBy (snd >> PartitionResult.toKey)

        let logErrorByKey = function
            | (NoErrorKey, _) -> ()
            | (Rule2ErrorKey, errors) -> 
                errors
                |> Array.map (mapSnd PartitionResult.toString)
                |> formatRule2Errors consumerGroup topic
                |> fun s -> log.Error("{s}", s)
            | (Rule3ErrorKey, errors) -> 
                errors
                |> Array.map (mapSnd PartitionResult.toString)
                |> formatRule3Errors consumerGroup topic
                |> fun s -> log.Error("{s}", s)

        match results with
        | [|NoErrorKey, _|] ->
            log.Information("Consumer seems OK|consumerGroup={0}|topic={1}", consumerGroup, topic)
        | errors ->
            errors |> Array.iter logErrorByKey

module private Helpers =

    // number of times we fail to get the consumer progress stats before the services crashes
    let [<Literal>] private MaxFailCount = 3

    let private topicPartitionIsForTopic (topic : string) (topicPartition : TopicPartition) =
        topicPartition.Topic = topic

    let private queryConsumerProgress (admin, consumer : IConsumer<_,_>) (topic : string) =
        consumer.Assignment
        |> Seq.filter (topicPartitionIsForTopic topic)
        |> Seq.map (fun topicPartition -> topicPartition.Partition.Value)
        |> Seq.toArray
        |> Legacy.ConsumerInfo.progress (admin,consumer) topic
        |> Async.map createPartitionInfoList

    let private logLatest (logger : ILogger) (consumerGroup : string) (topic : string) (Window partitionInfos) =
        let partitionOffsets =
            partitionInfos
            |> Seq.sortBy (fun p -> p.partition)
            |> Seq.map (fun p -> sprintf "(%i, %O, %O)" p.partition p.highWatermarkOffset p.consumerOffset)
            |> Strings.join " "

        let aggregateLag = partitionInfos |> Seq.sumBy (fun p -> p.lag)

        logger.Information("Consumer info|consumerGroup={0}|topic={1}|lag={2}|offsets={3}", consumerGroup, topic, aggregateLag, partitionOffsets)

    let private monitor consumer sleepMs (buffer : RingBuffer<_>) (logger : ILogger) (topic : string) (consumerGroup : string) handleErrors =
        let checkConsumerProgress () =
            queryConsumerProgress consumer topic
            |> Async.map (fun res ->
                buffer.SafeAdd res
                logLatest logger consumerGroup topic res
                let infoWindow = buffer.SafeFullClone()
                match infoWindow with
                | [||] -> ()
                | ci ->
                    Rules.checkRulesForAllPartitions ci
                    |> handleErrors consumerGroup topic)

        let handleFailure failCount exn =
            logger.Error("Problem getting ConsumerProgress|consumerGroup={group}|topic={topic}|failCount={failCount}", consumerGroup, topic, failCount)
            if failCount >= MaxFailCount then
                raise exn
            failCount + 1

        let rec monitor failCount = async {
            let! failCount = async {
                try
                    do! checkConsumerProgress()
                    return 0
                with
                | exn -> return handleFailure failCount exn
            }
            do! Async.Sleep sleepMs
            return! monitor failCount
        }
        monitor 0

    let resetBuffer (ringBuffer : RingBuffer<_>) (topic : string) (topicPartitions : seq<TopicPartition>) =
        if topicPartitions <> null then
            let topicRebalanced = topicPartitions |> Seq.exists (topicPartitionIsForTopic topic)
            if topicRebalanced then ringBuffer.Reset()

    let monitorTopics consumerInfos timeMS bufferSize logger handleErrors =
        consumerInfos
        |> Array.map (fun (consumer : IConsumer<_,_>, topic : string, consumerGroup : string) ->
            let ringBuffer = new RingBuffer<_>(bufferSize)
            let resetBufferForTopic = resetBuffer ringBuffer topic
            // Reset the ring buffer for this topic if there's a rebalance for the topic.
            consumer.OnPartitionsAssigned.Add(resetBufferForTopic)
            monitor consumer timeMS ringBuffer logger topic consumerGroup handleErrors)
            
    let monitorConsumer (consumer : IConsumer<_,_>, topic : string, consumerGroup : string) timeMS bufferSize logger handleErrors =
        let ringBuffer = new RingBuffer<_>(bufferSize)
        let resetBufferForTopic = resetBuffer ringBuffer topic
        // Reset the ring buffer for this topic if there's a rebalance for the topic.
        consumer.OnPartitionsAssigned.Add(resetBufferForTopic)
        monitor consumer timeMS ringBuffer logger topic consumerGroup handleErrors

/// The maximum number of topics the monitor can support.
let [<Literal>] private MaxTopics = 4

type MonitorConfig = {
    kafkaHost : string
    serviceName : string
    consumersAndTopics : (IConsumer<_,_> * string) []
    pollInterval : TimeSpan option
    windowSize : int option
    errorHandler : (string -> string -> (int * PartitionResult) [] -> unit) option
}

let private DefaultPollInterval = TimeSpan.FromSeconds 30.
let private DefaultWindowSize = 60

type KafkaMonitorConfig = {
    kafkaHost : string
    serviceName : string
    consumer : KafkaConsumer
    pollInterval : TimeSpan
    windowSize : int
    errorHandler : (string -> string -> (int * PartitionResult) [] -> unit)
} with
    static member Default (log : ILogger) (kafkaHost : string) (serviceName : string) (consumer : KafkaConsumer) = {
        KafkaMonitorConfig.kafkaHost = kafkaHost
        serviceName = serviceName
        consumer = consumer
        pollInterval = DefaultPollInterval
        windowSize = DefaultWindowSize
        errorHandler = Logging.logResults log
    }

let private runKafkaMonitor (log : ILogger, config : KafkaMonitorConfig) =
    let consumerInfo = (config.consumer.confluentConsumer, config.consumer.config.topic, config.consumer.config.consumerGroup)
    Helpers.monitorConsumer consumerInfo (int config.pollInterval.TotalMilliseconds) config.windowSize log config.errorHandler

/// Runs a Kafka monitor on a separate thread alongside the provided Async.
let runWithKafkaMonitor (config : KafkaMonitorConfig) (service : Async<unit>) =
    service |> Async.choose (runKafkaMonitor config)

/// Runs a Kafka monitor with the provided values, and default values for everything else, on a separate thread alongside the provided Async.
let runWithDefaultKafkaMonitor (host : string) (serviceName : string) (consumer : KafkaConsumer) (service : Async<unit>) =
    let config = KafkaMonitorConfig.Default host serviceName consumer
    service |> Async.choose (runKafkaMonitor config)   