@fog_coyote
Feature: libthread's channels keep their promises in every interleaving Coyote explores
  thread(2) promises that a send returns 1 once its message is received or buffered and -1 if it is
  interrupted or the channel is closed, and that recv returns -1 only when interrupted or the channel
  is closed and empty. So a message whose send returned 1 is received exactly once, a message whose
  send returned -1 is never received, and nothing is received that was not sent. Coyote runs each
  race many times in different orders and fails on the first that breaks a promise.

  @FOG_COYOTE_101
  Scenario Outline: Every message a send delivers is received exactly once, even as the channel closes
    Given a channel buffering <size> messages
    When 2 threads each send 2 messages and 2 threads receive until the channel is closed and empty, while another thread closes it
    Then in every explored schedule each message whose send returned 1 is received exactly once, and no other is received

    Examples:
      | size |
      | 0    |
      | 1    |
      | 3    |

  @FOG_COYOTE_102
  Scenario: An alt over two channels receives each of two racing sends exactly once
    Given 2 unbuffered channels
    When a thread alts twice on receiving from either while another thread sends on each
    Then in every explored schedule the alts receive one message from each channel

  @FOG_COYOTE_103
  Scenario: An interrupted receive either gets the message or leaves it for the next receive
    Given an unbuffered channel
    When a thread receives with an interrupt while another sends a message and a third interrupts the receive
    Then in every explored schedule the message is received exactly once and its send returns 1

  @FOG_COYOTE_104
  Scenario: An interrupted send either delivers its message or is never received
    Given an unbuffered channel
    When a thread sends a message with an interrupt while another receives and a third interrupts the send
    Then in every explored schedule the message is received if and only if its send returned 1

  @FOG_COYOTE_105
  Scenario: Closing one channel of an alt fails that entry while the other still delivers
    Given 2 unbuffered channels
    When a thread alts on receiving from either until it receives, while another thread closes the first and a third sends on the second
    Then in every explored schedule the message on the second channel is received exactly once and its send returns 1
