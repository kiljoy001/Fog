@libtab_jobs
Feature: Job text preserves the existing C and managed LibTab contract
  The service adds validation and authorization without inventing a new cell
  encoding or mistaking content-based row identity for job admission identity.

  @JOB_T01
  Scenario Outline: The published example documents are readable by both libraries
    Given the published <document> example with its final LF intact
    When the C and managed LibTab implementations read it independently
    Then both agree on its schema, columns, row count, and semantic cell values
    And deterministic re-emission matches the corresponding shared canonical fixture

    Examples:
      | document             |
      | job-math.tab         |
      | math-result.tab      |
      | job-wasm.tab         |
      | status-succeeded.tab |

  @JOB_T02 @property
  Scenario Outline: Plain metadata values keep their ndb meaning
    Given a valid status error cell containing <value>
    When each library serializes that cell and the other library reads it
    Then its semantic value round-trips unchanged
    And both libraries agree whether the cell is absent or text

    Examples:
      | value                    |
      | semantic nil             |
      | the literal text nil     |
      | empty text               |
      | Unicode text             |
      | quotes and ampersands    |
      | a newline                |
      | a leading hash character |

  @JOB_T03 @property
  Scenario Outline: Encoded cell size is enforced rather than decoded string length
    Given a candidate document whose largest canonical emitted cell line is <bytes> bytes
    And all other document and semantic constraints are satisfied
    When the service validates it against the LibTab cell limit
    Then the cell-size decision is <decision>
    And entity encoding, UTF-8, column names, indentation, and LF are included in the count

    Examples:
      | bytes | decision |
      | 7167  | accept   |
      | 7168  | accept   |
      | 7169  | reject   |

  @JOB_T04 @security @fuzz
  Scenario: A short raw value cannot hide expansion past the emitted cell limit
    Given a candidate plain cell containing enough raw ampersands to exceed the limit after encoding
    And its incoming physical line is shorter than the line limit
    When the service validates the complete document
    Then it rejects the cell before publication or job admission
    And it directs large content to a separate file reference without changing LibTab encoding

  @JOB_T05
  Scenario: Large payloads remain raw files rather than oversized table cells
    Given a valid job whose source or input file is larger than a LibTab cell
    And that file is within the job's artifact policy and preparation limits
    When the job references it through an authorized namespace path
    Then the specification remains a bounded readable LibTab document
    And validation pins the exact artifact without embedding it in a cell

  @JOB_T06 @security
  Scenario: Signing one cell does not authenticate changed sibling fields
    Given an artifact manifest with a valid SIGNED cell
    When an attacker changes an unsigned runtime, artifact reference, or authority field
    Then the service does not infer whole-manifest authenticity from the signed cell
    And a mismatch with authenticated manifest content rejects the job

  @JOB_T07 @security
  Scenario: Signature verification requires an authorized key and bound content
    Given an artifact manifest required by policy to authenticate a job and artifact digest
    When its signer label is unknown or its verified content does not match the frozen job
    Then validation fails before artifact execution
    And the label alone is not resolved into trust or access authority

  @JOB_T08 @security @fuzz
  Scenario: Malformed or expensive crypto metadata remains bounded
    Given an external manifest with HASHED, SIGNED, or ENCRYPTED cells
    When those cells are malformed, unverifiable, or exceed configured processing limits
    Then the manifest is rejected within bounded resource use
    And no unverified plaintext or job capability is returned
    And no runtime starts based on that manifest

  @JOB_T09 @cluster @wire
  Scenario: C and managed clients submit through the same files
    Given a real two-silo 9P-only swarm with deterministic fixture workloads
    When a C LibTab client and a managed LibTab client each submit a job through 9P files
    Then both workflows retain the same allocation, validation, control, and status semantics
    And the expected results are obtained through the ordinary result files
    And no JSON, HTTP, or native Orleans client protocol is required from those job clients
