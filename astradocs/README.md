# AstraDocs: get comfortable with this codebase

The **[implementation bootcamp](bootcamp/README.md)** follows all 75 stories
through code, lessons, verification, and a continuing engineering journal.
Check its ledger for actual completion rather than treating a plan as finished.
Use the readable [story map](bootcamp/story-map.md) when you want to jump from a
specific story to its plan, lesson, implementation, and current status.

Start small. You can read one page, try one thing, and stop. These guides
revisit the same ideas as a story, a diagram, real code, questions, and
hands-on tasks. Use whichever explanation helps today; revisit another later.

You do not need to memorize filenames or understand every feature to belong
in this repository. The first goal is to find your way through one request.

## Start with just these three things

1. Read [your first 20 minutes](01-first-20-minutes.md).
2. Read [one event's journey](04-one-event-story.md).
3. Open [the visual walkthrough](index.html) in your browser and step through it.

The browser walkthrough works locally, without the API, an account, or an
internet connection. Markdown links open best in your editor's preview.
When you are ready to run the API, follow the current disposable-database build
and explicit-upgrade steps in [the first backend session](../docs/learning/01-first-session.md).
Ordinary startup checks schema currency; it does not create or upgrade a database.

## The five ideas we will keep returning to

1. A **project** groups analytics data and controls access to it.
2. A **User** operates Pulse; a **Person** represents someone tracked by a product.
3. **Capture** saves queued work before returning `202 Accepted`.
4. A **worker** processes queued work into events or records failures.
5. **Queries** answer questions using processed events.

You will see these same words throughout. If you forget one, look it up and
continue. There is no reading speed requirement.

## Pick the explanation you need

| I would like to... | Open |
| --- | --- |
| Understand what we are building | [The big picture, three ways](02-big-picture.md) |
| Know where files live | [Repository map and feature finder](03-repository-map.md) |
| Follow a person using a product | [One event's journey](04-one-event-story.md) |
| See what changes in the data | [The same event in four shapes](05-data-shapes.md) |
| Understand individual C# lines | [Read the capture code slowly](06-code-close-up.md) |
| Understand how the app starts | [Startup and dependency wiring](07-startup-and-wiring.md) |
| Stop mixing up names and IDs | [People, projects, and tables](08-people-and-tables.md) |
| Understand credentials and errors | [Keys and permissions](09-keys-and-permissions.md) |
| Recognize the other features | [A tour of the rest of Pulse](10-feature-tour.md) |
| Run a test and understand it | [Tests as worked examples](11-tests-as-examples.md) |
| Make my first small contribution | [A guided first change](12-first-change.md) |
| Figure out what went wrong | [When you get stuck](13-when-stuck.md) |
| Hear someone ask the questions I have | [A mentor conversation](14-mentor-conversation.md) |
| Check what I remember | [Recall cards with answers](15-recall-cards.md) |
| Have a gentle sequence to follow | [Ten short sessions](16-session-plan.md) |
| Quickly refresh the essentials | [One-page desk reference](17-desk-reference.md) |

## How to use repetition

Read an explanation. Close it and say one sentence in your own words. Look
again when you need to. Try a different representation next, then return to
the code. A useful question is "Which part is still unclear?", rather than
"Why don't I understand everything yet?"

These are source-based onboarding guides. The earlier implementation has
an [unresolved test-validation record](../docs/learning/validation.md).
Expected outputs here describe behavior to check, not a claim that all tests
currently pass on every machine.

When you want larger engineering exercises, continue into the existing
[learning curriculum](../docs/learning/README.md).
