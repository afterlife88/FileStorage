﻿using System.Collections.Generic;
using System.Threading.Tasks;
using FileStorage.Domain.Entities;

namespace FileStorage.Domain.Infrastructure.Repositories
{
    public interface IUserRepository
    {
        Task<IEnumerable<ApplicationUser>> GetAllUsersAsync();
        Task CreateAsync(ApplicationUser user, string password);
        Task<ApplicationUser> GetUserByNameAsync(string username);
        Task EditAsync(ApplicationUser user);
        Task<ApplicationUser> GetUser(string userEmail);
        Task<ApplicationUser> GetUserByID(string userId);
    }
}
