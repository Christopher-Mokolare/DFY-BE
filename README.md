# DoForYou API

A fully integrated .NET 8 Web API for the DoForYou task management platform that works with all frontend components.

## Quick Setup

1. **Run the setup script**:
   ```bash
   ./setup.sh
   ```

2. **Update database connection** in `appsettings.json` if needed:
   ```json
   "DefaultConnection": "Host=localhost;Database=doforyou;Username=your_username;Password=your_password"
   ```

3. **Run the API**:
   ```bash
   dotnet run
   ```

## Complete API Endpoints

### Authentication
- `POST /api/v1/auth/register` - User registration
- `POST /api/v1/auth/login` - User login

### Tasks
- `POST /api/v1/tasks` - Create task with PayFast integration
- `GET /api/v1/tasks/available` - Browse available tasks (paginated)
- `GET /api/v1/tasks/my-posted` - User's posted tasks
- `GET /api/v1/tasks/my-active` - User's active tasks as runner
- `GET /api/v1/tasks/{taskId}` - Get task details with progress
- `POST /api/v1/tasks/{taskId}/claim` - Claim a task
- `POST /api/v1/tasks/{taskId}/complete` - Mark task as completed
- `PATCH /api/v1/tasks/{taskId}/payment-status` - Update payment status
- `GET /api/v1/tasks/filters` - Get filter options

### Communication
- `POST /api/v1/tasks/{taskId}/messages` - Send message
- `GET /api/v1/tasks/{taskId}/messages` - Get task messages
- `POST /api/v1/tasks/{taskId}/progress` - Add progress update
- `GET /api/v1/tasks/{taskId}/progress` - Get progress updates

### User Management
- `GET /api/v1/users/profile` - Get user profile
- `PUT /api/v1/users/profile` - Update user profile
- `GET /api/v1/users/dashboard/stats` - Dashboard statistics

### Categories
- `GET /api/v1/categories` - Get all categories

### Payment Integration
- `POST /api/v1/payment/notify` - PayFast webhook
- `GET /api/v1/payment/return` - PayFast return URL
- `GET /api/v1/payment/cancel` - PayFast cancel URL

### Admin (Role: Admin)
- `GET /api/v1/admin/tasks` - Get all tasks with filters
- `POST /api/v1/admin/tasks/{taskId}/verify` - Verify task payment
- `POST /api/v1/admin/tasks/{taskId}/unverify` - Unverify task payment

## Features

✅ **Complete Frontend Integration**
- All ErrandsService endpoints implemented
- All AuthService endpoints implemented
- All CategoryService endpoints implemented
- All TaskService endpoints implemented
- All PaymentService endpoints implemented

✅ **Authentication & Authorization**
- JWT token authentication
- Role-based authorization (User, Admin)
- BCrypt password hashing

✅ **Task Management**
- DFY-formatted task IDs (`DFY-{timestamp}-{random}`)
- Payment status tracking
- Task status workflow (PendingPayment → Posted → Claimed → Completed)
- Task claiming and completion

✅ **Payment Integration**
- PayFast sandbox integration
- Automatic task posting after payment
- Payment webhook handling

✅ **Communication**
- Task messaging between creator and runner
- Progress updates from runners
- Real-time communication support

✅ **Database**
- PostgreSQL with Entity Framework Core
- Complete schema matching frontend expectations
- Database seeding with categories and admin user

✅ **API Features**
- CORS enabled for Angular frontend
- Swagger documentation
- Pagination support
- Search and filtering
- Error handling

## Default Credentials

**Admin User:**
- Email: `admin@doforyou.com`
- Password: `<set_via_env>`

## Development

The API runs on `http://localhost:5001` and is configured to work with the Angular frontend on `http://localhost:4200`.

Swagger documentation is available at `http://localhost:5001/swagger` in development mode.