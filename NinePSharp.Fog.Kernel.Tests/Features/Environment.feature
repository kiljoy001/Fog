@fog_kernel_environment
Feature: The environment is devenv's, each variable its group's to write whoever made it
  9front's devenv keeps no owner for a variable: each is the host owner's, with the mode 666 for
  the processes of its environment group, and is made so whatever mode its creator asked for. So
  a process that has become another user, as auth_chuid makes it before newns copies the
  environment, writes the variables it was given.

  Background:
    Given a kernel booted for the host owner "glenda" whose environment holds "user" set to "glenda"

  @FOG_KERNEL_080
  Scenario Outline: A process that has become another user writes the group's variables
    When a child that becomes "none" writes "none" to "<path>"
    Then the child's write succeeds
    And "<path>" holds "none"
    And "<path>" belongs to "glenda" with the mode 666

    Examples:
      | path      |
      | /env/user |
      | /env/home |
