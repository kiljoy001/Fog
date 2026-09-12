@fog_v1_profiles @security
Feature: An independent Linux supervisor owns the execution boundary
  A cooperative cancellation token or a guest status message is not proof of containment.

  @FOG_V1_ISO01
  Scenario Outline: Missing containment mechanisms fail closed
    Given a candidate execution host without working <mechanism>
    When it validates its runtime registrations
    Then it becomes ineligible before executing any guest input
    And it does not substitute an unrestricted in-process runner

    Examples:
      | mechanism                  |
      | cgroup memory enforcement  |
      | cgroup pids enforcement    |
      | cgroup kill                |
      | pidfd observation          |
      | namespace isolation        |
      | seccomp policy installation |
      | delegated cgroup ownership |

  @FOG_V1_ISO02
  Scenario: A duplicate launch and run has one execution owner
    Given an admitted execution key and a verified installed bundle
    When launch and run commands are retried or race
    Then exactly one process and one RunAsync invocation own that key
    And no second cgroup, memory reservation or execution is created

  @FOG_V1_ISO03 @property
  Scenario: Forwarding delay cannot extend supervisor deadlines
    Given a verified lease timed before a node-host forwarding delay
    When the supervisor receives its absolute CLOCK_BOOTTIME deadline
    Then it uses that original deadline rather than receipt time
    And an expired renewal cannot revive stopped execution
    And host suspend time counts toward expiry

  @FOG_V1_ISO04
  Scenario Outline: Real native escape probes cannot reach ambient authority
    Given an installed adversarial test runner inside linux-process-v1
    When it attempts <escape> after the startup seal
    Then the real OS boundary denies or terminates the attempt
    And the probe cannot affect the protected host resource

    Examples:
      | escape                              |
      | opening a host credential file      |
      | connecting a raw network socket     |
      | entering another namespace          |
      | invoking a new executable            |
      | tracing a host process              |
      | creating a child process instead of a thread |
      | raising its memory or descriptor limit |
      | opening a GPU device                |

  @FOG_V1_ISO05 @wire @fuzz
  Scenario: Private 9P capabilities cannot be selected through guest input
    Given private worker-control and capability-broker descriptors bound to one execution key
    When guest input or a malformed private request claims another job or supervisor command
    Then it cannot change the descriptor's bound authority or launch work
    And public TCP listeners never accept the private NOFID trust exception

  @FOG_V1_ISO06
  Scenario: A hung grain host does not stop the watchdog
    Given an independent supervisor with a running noncooperative native worker
    When the Orleans host stops making progress and the original lease expires
    Then the supervisor revokes broker access and requests containment termination
    And it observes the owned process through its pidfd without a grain call
    And no successful terminal outcome is published from the guest's own status

  @FOG_V1_ISO07 @property
  Scenario: PID reuse cannot redirect teardown to an unrelated process
    Given an owned worker exits while an unrelated process reuses its numeric PID
    When delayed cleanup runs
    Then it uses the original pidfd and recorded cgroup ownership
    And it cannot terminate or reap the unrelated process as its worker

  @FOG_V1_ISO08
  Scenario: Success waits for complete output and reconciled execution
    Given a runner reports completed while its process or private handles remain live
    When the node host considers terminal publication
    Then success remains unavailable until verified output and supervisor reaping complete
    And only bounded result bytes remain after cleanup

  @FOG_V1_ISO09
  Scenario: Unkillable work quarantines capacity instead of fabricating cleanup
    Given process termination cannot be established by the configured hard stop deadline
    When the supervisor reaches that deadline
    Then it marks cleanup-failed and quarantines the host or affected execution capacity
    And the reservation is not reused or reported as successfully reaped
    And restart cannot advertise capacity until ownership is reconciled

  @FOG_V1_ISO10
  Scenario: Supervisor death does not leave a reusable orphan execution
    Given a supervised process with parent-death protection and recorded cgroup ownership
    When the supervisor dies and its replacement starts
    Then the owned process is terminated or explicitly reconciled before readiness
    And unrelated host cgroups are not treated as cleanup targets
    And no old execution key becomes a fresh launch

  @FOG_V1_ISO11
  Scenario Outline: Real resource caps cover native runtime overhead too
    Given a runner with a finite <resource> allowance
    When it attempts to exceed that allowance under OS enforcement
    Then the attempt is bounded or the job fails without successful partial output
    And no new unlimited host resource is substituted

    Examples:
      | resource             |
      | whole-process memory |
      | threads and processes |
      | open descriptors     |
      | private temporary storage |
      | stderr bytes         |
