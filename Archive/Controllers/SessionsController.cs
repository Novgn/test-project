/* ARCHIVED - No longer in use
using Microsoft.AspNetCore.Mvc;
using ChatAgent.Domain.Interfaces;
using ChatAgent.Domain.Entities;

namespace ChatAgent.WebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SessionsController : ControllerBase
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly ILogger<SessionsController> _logger;

        public SessionsController(
            IConversationRepository conversationRepository,
            ILogger<SessionsController> logger)
        {
            _conversationRepository = conversationRepository;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<object>>> GetSessions()
        {
            _logger.LogInformation("Getting all active conversation sessions");

            var conversations = await _conversationRepository.GetActiveConversationsAsync();

            var sessions = conversations.Select(c => new
            {
                id = c.SessionId,
                name = $"Session {c.SessionId.Substring(0, Math.Min(8, c.SessionId.Length))}",
                createdAt = c.StartedAt,
                messageCount = c.Messages.Count,
                status = c.Status.ToString()
            });

            return Ok(sessions);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<object>> GetSession(string id)
        {
            _logger.LogInformation("Getting conversation session {SessionId}", id);

            var conversation = await _conversationRepository.GetBySessionIdAsync(id);

            if (conversation == null)
            {
                return NotFound();
            }

            return Ok(new
            {
                id = conversation.SessionId,
                messages = conversation.Messages.Select(m => new
                {
                    role = m.Role.ToString(),
                    content = m.Content,
                    timestamp = m.Timestamp
                }),
                status = conversation.Status.ToString(),
                startedAt = conversation.StartedAt,
                endedAt = conversation.EndedAt
            });
        }

        [HttpPost]
        public async Task<ActionResult<object>> CreateSession()
        {
            _logger.LogInformation("Creating new conversation session");

            var sessionId = Guid.NewGuid().ToString();
            var conversation = new Conversation(sessionId);

            await _conversationRepository.CreateAsync(conversation);

            return Ok(new
            {
                id = sessionId,
                name = $"Session {sessionId.Substring(0, 8)}",
                createdAt = conversation.StartedAt
            });
        }

        [HttpPut("{id}/end")]
        public async Task<ActionResult> EndSession(string id)
        {
            _logger.LogInformation("Ending conversation session {SessionId}", id);

            var conversation = await _conversationRepository.GetBySessionIdAsync(id);

            if (conversation == null)
            {
                return NotFound();
            }

            conversation.End();
            await _conversationRepository.UpdateAsync(conversation);

            return Ok();
        }
    }
}*/
