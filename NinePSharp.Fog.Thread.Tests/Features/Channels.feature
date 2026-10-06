@fog_thread
Feature: Channels and alt behave as 9front's libthread describes in thread(2)
  A Channel is a buffered or unbuffered queue for messages. If the channel is unbuffered, a send
  blocks until the corresponding recv occurs, and vice versa; a buffered channel holds up to its
  size of messages. Send and recv return 1 on success and -1 if interrupted; nbsend and nbrecv
  return 0 rather than blocking. Alt executes one of an array of sends and receives, chosen at
  random among those that can proceed, returns the index of the CHANNOBLK terminator when none can
  and it may not block, and otherwise blocks until one can. Chanclose prevents further sends: after
  it, send and recv never block, send returns -1, and recv returns -1 once the channel is empty.
  Alt may choose an operation that failed because its channel was closed, marking the entry's
  error, and returns -1 when every entry failed that way. A .NET task stands in for a thread, and
  cancelling its token interrupts it as threadint does. A channel belongs to a rendezvous group, as a
  libthread program's channels belong to its process, and an alt's channels must share one.

  @FOG_THREAD_001
  Scenario: A send on an unbuffered channel waits for a recv, and the recv gets its value
    Given an unbuffered channel
    When a thread sends 7
    Then the send has not finished
    When another thread receives
    Then the receive gets 7 and returns 1
    And the send returns 1

  @FOG_THREAD_002
  Scenario: A recv on an unbuffered channel waits for a send
    Given an unbuffered channel
    When a thread receives
    Then the receive has not finished
    When another thread sends 9
    Then the receive gets 9 and returns 1

  @FOG_THREAD_003
  Scenario: A buffered channel holds its size of messages, then a send waits
    Given a channel buffering 2 messages
    When a thread sends 1, 2 and 3 in turn
    Then the first 2 sends have returned 1 and the third has not finished
    When another thread receives 3 times
    Then it gets 1, 2 and 3 in order
    And the third send returns 1

  @FOG_THREAD_004
  Scenario Outline: nbsend and nbrecv return 0 instead of blocking
    Given <channel>
    When a thread tries a non-blocking <operation>
    Then it returns 0 at once

    Examples:
      | channel                        | operation |
      | an unbuffered channel          | send      |
      | an unbuffered channel          | recv      |
      | a channel buffering 1 messages | recv      |
      | a full channel buffering 1 message | send  |

  @FOG_THREAD_005
  Scenario: alt chooses at random among the operations that can proceed
    Given 2 channels each buffering 1 message, both holding a message
    When a thread alts on receiving from both, 200 times, refilling the chosen channel each time
    Then each channel is chosen at least once

  @FOG_THREAD_006
  Scenario: alt that may not block returns the index of its CHANNOBLK terminator
    Given an unbuffered channel
    When a thread alts on receiving from it, terminated by CHANNOBLK
    Then alt returns 1, the index of the terminator, at once

  @FOG_THREAD_007
  Scenario: alt blocks until one operation can proceed, skipping CHANNOP entries
    Given 2 unbuffered channels
    When a thread alts on a CHANNOP entry and on receiving from each channel
    Then the alt has not finished
    When another thread sends 5 on the second channel
    Then alt returns 2 and its entry received 5

  @FOG_THREAD_008
  Scenario: alt can send as well as receive
    Given an unbuffered channel
    When a thread alts on sending 4 on it
    And another thread receives
    Then alt returns 0 and the receive gets 4

  @FOG_THREAD_009
  Scenario: After chanclose a send fails at once, and a blocked sender is woken with -1
    Given an unbuffered channel
    When a thread sends 1
    And the channel is closed
    Then the send returns -1
    And another send returns -1 at once

  @FOG_THREAD_010
  Scenario: After chanclose the buffered messages are still received, then recv fails
    Given a channel buffering 2 messages
    When a thread sends 1 and 2 in turn
    And the channel is closed
    Then 2 receives get 1 and 2
    And another receive returns -1 at once

  @FOG_THREAD_011
  Scenario: A recv blocked on an empty channel is woken with -1 when it closes
    Given an unbuffered channel
    When a thread receives
    And the channel is closed
    Then the receive returns -1

  @FOG_THREAD_012
  Scenario: alt selects each entry whose channel was closed once, then fails
    An entry's error survives between alts on the same entries, as the Alt structure's err does.
    Given 2 unbuffered channels
    When a thread alts on receiving from each channel
    And the first channel is closed
    Then alt returns 0 and that entry's error says the channel was closed
    When the second channel is closed
    And the thread alts with the same entries again
    Then alt returns 1 and that entry's error says the channel was closed
    When the thread alts with the same entries again
    Then alt returns -1

  @FOG_THREAD_013
  Scenario: chanclose of a closed channel fails, and chanclosing reports what is left
    Given a channel buffering 2 messages
    When a thread sends 1
    Then chanclosing returns -1
    When the channel is closed
    Then chanclosing returns 1
    And closing it again returns -1

  @FOG_THREAD_014
  Scenario: Interrupting a blocked operation makes it return -1, and nothing is lost
    Given an unbuffered channel
    When a thread sends 8 and is interrupted before anyone receives
    Then the send returns -1
    When another thread tries a non-blocking recv
    Then it returns 0 at once

  @FOG_THREAD_015
  Scenario Outline: nbsend and nbrecv proceed at once when a partner is waiting
    Given an unbuffered channel
    When a thread <waits>
    And another thread tries a non-blocking <operation>
    Then it returns 1 at once
    And the waiting thread's <waited> returns 1 with 6

    Examples:
      | waits     | operation | waited  |
      | receives  | send      | receive |
      | sends 6   | recv      | send    |

  @FOG_THREAD_016
  Scenario: alt waits on its open channel even when another of its channels is closed
    Given 2 unbuffered channels
    When the first channel is closed
    And a thread alts on receiving from each channel
    Then the alt has not finished
    When another thread sends 5 on the second channel
    Then alt returns 1 and its entry received 5

  @FOG_THREAD_017
  Scenario: alt on CHANNOP entries alone waits until it is interrupted
    When a thread alts on 2 CHANNOP entries
    Then the alt has not finished
    When the alt is interrupted
    Then alt returns -1

  @FOG_THREAD_018
  Scenario: Two senders on an unbuffered channel both wait, and both values are received
    Given an unbuffered channel
    When a thread sends 1
    And another thread sends 2
    Then neither send has finished
    When another thread receives 2 times
    Then it gets 1 and 2 in some order
    And both sends return 1

  @FOG_THREAD_019
  Scenario: A recv waiting on an empty buffered channel gets the next send directly
    Given a channel buffering 2 messages
    When a thread receives
    And another thread sends 3
    Then the receive gets 3 and returns 1
    And chanclosing still reports the channel open

  @FOG_THREAD_020
  Scenario: Messages keep their order when the buffer wraps around under a waiting sender
    Given a channel buffering 2 messages
    When a thread sends 1, 2, 3 and 4, receiving one message after the second
    Then the sends of 1, 2 and 3 have returned 1 and the send of 4 has not finished
    When another thread receives 3 times
    Then it gets 2, 3 and 4 in order
    And the fourth send returns 1

  @FOG_THREAD_021
  Scenario: Closing a full buffered channel wakes its blocked sender, and its messages remain
    Given a full channel buffering 1 message
    When a thread sends 1
    And the channel is closed
    Then the send returns -1
    And a receive gets 0 and returns 1
    And another receive returns -1 at once

  @FOG_THREAD_022
  Scenario: An alt over channels of different rendezvous groups is refused
    Given 2 unbuffered channels in different rendezvous groups
    When a thread alts on receiving from each channel
    Then the alt is refused because "an alt's channels must share a rendezvous group"
