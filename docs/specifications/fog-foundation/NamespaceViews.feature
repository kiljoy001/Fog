@fog_foundation @namespace_authorization @design
Feature: Fog serves each authenticated principal its own namespace view
  The direct enrolled-node TLS export builds a principal's view from an in-memory policy
  model, using the existing process-group and mount-table machinery and the resource
  authorization layer. LibTab policy bundles, epoch administration and job scopes are
  later slices.
  These scenarios require executable bindings before they count as implemented.

  @FOG_VIEW_001 @FOG_N01
  Scenario: An attach builds only the configured view for its principal
    Given two enrolled principals with different mount profiles
    When each authenticates and attaches
    Then each root contains only its own configured mounts
    And each attach owns a distinct process group
    And no attach name or claimed user selects another principal's view

  @FOG_VIEW_002
  Scenario: A view starts empty and is built in configured order
    Given a profile with one root replace mount followed by before and after mounts
    When the view is built
    Then mounts are applied in ascending order with their configured placement
    And a profile without exactly one leading root replace mount is rejected

  @FOG_VIEW_003
  Scenario: Dot-dot is clamped at the exported root
    Given an attached principal at its root
    When it walks ".." any number of times
    Then it remains at its root and sees no resource above it

  @FOG_VIEW_004
  Scenario: The control tree is one mount in the view
    Given a profile that mounts the Fog control tree at /control
    When the principal clones, uploads, commits and reads a transaction
    Then the existing control-file behaviour is unchanged
    And a profile without that mount has no /control

  @FOG_VIEW_005 @FOG_N09
  Scenario Outline: Unconfigured infrastructure paths are absent
    Given a principal whose profile does not mount <path>
    When it walks to <path>
    Then the walk reports not found

    Examples:
      | path               |
      | /control           |
      | /transport/orleans |
      | /admin/ctl         |
      | /compute/clone     |

  @FOG_VIEW_006
  Scenario: Directory reads in a view never use raw provider streams
    Given a provider that offers raw directory streaming
    When the principal reads a directory in its view
    Then entries come from authorized metadata reads
    And hidden entries are absent

  @FOG_VIEW_007
  Scenario: Replacing the policy invalidates fids from the old generation
    Given a principal with open fids under the current policy
    When the host replaces the policy
    Then those fids are denied
    And a fresh attach builds its view from the new policy
