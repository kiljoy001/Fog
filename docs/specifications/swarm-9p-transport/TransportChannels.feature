@swarm_transport
Feature: Orleans messages travel through a faithful duplex 9P stream
  The channel preserves bytes, order, and independent progress in both directions.
  It does not reinterpret successful transport writes as completed grain calls.

  @SW9P_C01 @property @wire
  Scenario Outline: Network chunking does not change the byte stream
    Given an open Orleans channel with negotiated msize 4096
    And distinct binary payloads larger than msize in both directions
    When the network delivers frames using <delivery>
    Then each receiver obtains exactly the corresponding payload in order
    And no byte is duplicated, dropped, or delivered to the opposite stream
    And every serialized 9P message fits the negotiated msize

    Examples:
      | delivery                             |
      | one byte at a time                    |
      | several complete frames in one chunk |
      | splits inside headers and payloads    |
      | generated fragment and batch sizes   |

  @SW9P_C02
  Scenario: An idle read does not prevent a write from completing
    Given an open Orleans channel with no server-to-client data available
    And the client has a positive-length Tread waiting for data
    When the client writes a request through the same channel
    Then the server accepts the written bytes while the read is still outstanding
    And the server can produce the response that completes the waiting read
    And neither direction waits for the other direction to become idle

  @SW9P_C03 @property
  Scenario: A short read exposes only the bytes actually returned
    Given an open Orleans channel with a generated positive-length payload available
    When the peer returns that payload through short Rread responses
    Then the adapter delivers exactly the returned bytes in order
    And it does not add padding or treat a positive short read as EOF
    And it issues another read when the consumer requests more data

  @SW9P_C04 @property
  Scenario: A positive short write retries only the unaccepted suffix
    Given an open Orleans channel and a generated payload to write
    And the peer acknowledges a positive prefix shorter than that payload
    When the adapter continues the write
    Then its next Twrite contains only the unaccepted suffix
    And the complete payload is accepted exactly once in order

  @SW9P_C05 @security
  Scenario Outline: Invalid write progress fails the channel instead of spinning
    Given an open Orleans channel with a nonempty write outstanding
    When the peer responds with <acknowledgement>
    Then the adapter fails that channel with a protocol error
    And the adapter does not retry the same bytes indefinitely
    And pending operations on the channel terminate

    Examples:
      | acknowledgement                      |
      | a zero Rwrite count                  |
      | an Rwrite count larger than the data |

  @SW9P_C06
  Scenario: Idle, zero-length read, and EOF have different meanings
    Given an open Orleans channel with no buffered data
    When the client issues a zero-length Tread
    Then it receives an empty Rread without waiting or consuming future data
    When the client issues a positive-length Tread
    Then that read remains pending while the producer is idle
    When the producer closes its output after its buffered bytes are drained
    Then the positive-length read returns EOF
    And the adapter reports end of stream instead of polling forever

  @SW9P_C07 @property
  Scenario: Independent connections isolate identical fid and tag numbers
    Given two authorized connections with the same numeric channel fid and request tag
    And the connections carry different generated payloads
    When their read and write requests are interleaved
    Then each connection receives only its own payloads and responses
    And closing either connection does not close the other channel

  @SW9P_C08
  Scenario: The channel is a stream rather than a seekable replay buffer
    Given an open Orleans channel
    When successive writes use the same offset with different payloads
    Then both payloads are appended to the receiving stream in submission order
    When successive reads use the same offset
    Then reads consume the next available bytes rather than replaying old bytes

  @SW9P_C09
  Scenario: Transport acknowledgement does not imply grain completion
    Given a channel whose receiver accepts an encoded grain request
    And the grain is held at a controlled execution barrier
    When the sender receives the corresponding Rwrite
    Then the stream write is acknowledged
    But the grain call remains incomplete until its actual Orleans response arrives

  @SW9P_C10 @property @wire
  Scenario Outline: Open IO limits constrain transfers independently of msize
    Given an open Orleans channel with negotiated msize 4096 and iounit <iounit>
    When the adapter transfers a binary payload larger than msize in both directions
    Then every frame fits msize including its headers
    And nonzero iounit also bounds the data size of each transfer request
    And the complete payload is delivered without truncation

    Examples:
      | iounit |
      | 0      |
      | 128    |
      | 8192   |
