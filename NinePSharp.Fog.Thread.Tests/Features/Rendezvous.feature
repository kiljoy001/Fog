@fog_thread
Feature: Threads rendezvous on a tag as libthread's _threadrendezvous does
  Two threads meeting on the same tag exchange values: the first to arrive sleeps until the
  second comes, and each returns the other's value. A sleeper that is interrupted leaves the tag
  and returns ~0, which therefore is not used as an ordinary value. Tags are compared by identity,
  and only within one rendezvous group, as rfork's RFREND separates processes' tags.

  @FOG_THREAD_101
  Scenario: The first thread waits, and the two exchange values
    Given a tag
    When a thread rendezvouses on the tag with "first"
    Then that rendezvous has not finished
    When another thread rendezvouses on the tag with "second"
    Then the second rendezvous returns "first" at once
    And the first rendezvous returns "second"

  @FOG_THREAD_102
  Scenario: Different tags do not meet, even when they are equal values
    Given a tag
    And another tag equal to it but a different object
    When a thread rendezvouses on the tag with "first"
    And another thread rendezvouses on the other tag with "second"
    Then neither rendezvous has finished

  @FOG_THREAD_103
  Scenario: An interrupted sleeper returns ~0 and leaves the tag free
    Given a tag
    When a thread rendezvouses on the tag with "first" and is interrupted
    Then that rendezvous returns ~0
    When another thread rendezvouses on the tag with "second"
    Then that rendezvous has not finished

  @FOG_THREAD_104
  Scenario: Threads in different rendezvous groups do not meet, even on the same tag
    Given a tag
    When a thread rendezvouses on the tag with "first"
    And another thread in another rendezvous group rendezvouses on the tag with "second"
    Then neither rendezvous has finished
