﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using FileStorage.DAL.Contracts;
using FileStorage.Domain.Entities;
using FileStorage.Services.Contracts;
using FileStorage.Services.DTO;
using FileStorage.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace FileStorage.Services.Services
{
    /// <summary>
    /// Service for manage files
    /// </summary>
    public class FileService : IFileService
    {
        #region Variables

        private readonly IUnitOfWork _unitOfWork;
        private readonly IBlobService _blobService;

        #endregion
        /// <summary>
        /// Model state of the executed actions
        /// </summary>
        public ServiceState State { get; }

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="unitOfWork"></param>
        /// <param name="blobService"></param>
        public FileService(IUnitOfWork unitOfWork, IBlobService blobService)
        {
            _unitOfWork = unitOfWork;
            _blobService = blobService;
            State = new ServiceState();
        }
        #region Methods

        public async Task<IEnumerable<NodeDto>> GetUserFiles(string userEmail)
        {
            var owner = await _unitOfWork.UserRepository.GetUserAsync(userEmail);
            var files = await _unitOfWork.NodeRepository.GetAllNodesForUser(owner.Id);

            var filesWithoutFolders = files.Where(r => r.IsDirectory == false);
            return Mapper.Map<IEnumerable<Node>, IEnumerable<NodeDto>>(filesWithoutFolders);
        }

        public async Task<Tuple<Stream, NodeDto>> GetFile(Guid uniqFileId, string callerEmail, int? versionOfFile)
        {
            var owner = await _unitOfWork.UserRepository.GetUserAsync(callerEmail);
            var getFileNode = await _unitOfWork.NodeRepository.GetNodeById(uniqFileId);

            if (getFileNode == null)
            {
                State.TypeOfError = TypeOfServiceError.NotFound;
                State.ErrorMessage = "Requested file is not found!";
                return Tuple.Create<Stream, NodeDto>(null, null);
            }
            // TODO: Later check share emails
            if (owner.Id != getFileNode.OwnerId)
            {
                State.TypeOfError = TypeOfServiceError.Unathorized;
                State.ErrorMessage = "You are not authorized to get this file!";
                return Tuple.Create<Stream, NodeDto>(null, null);
            }

            // Version of file not passed - then return last version
            if (versionOfFile == null)
                return await GetLastVersionOfFile(getFileNode);
            return await GetConcreteVersionOfFile(getFileNode, versionOfFile.GetValueOrDefault(-1));
        }

        public async Task<ServiceState> UploadAsync(IFormFile file, string directoryName, string userEmail)
        {
            try
            {
                if (file == null)
                {
                    State.ErrorMessage = "No file attached!";
                    State.TypeOfError = TypeOfServiceError.BadRequest; ;
                    return State;
                }

                var callerUser = await _unitOfWork.UserRepository.GetUserAsync(userEmail);

                Node directoryWhereFileUploadTo;
                if (directoryName == null)
                    directoryWhereFileUploadTo = await _unitOfWork.NodeRepository.GetRootFolderForUser(callerUser.Id);
                else
                    directoryWhereFileUploadTo = await _unitOfWork.NodeRepository.GetNodeByName(directoryName);

                // Validate current Node (folder that file uploading to) 
                if (!ValidateNode(State, directoryWhereFileUploadTo, callerUser))
                {
                    return State;
                }
                // Check if file with concrete hash already exist in service
                string md5Hash = GetMD5HashFromFile(file);
                var checkIsFileWithHashExist = await _unitOfWork.FileVersionRepository.GetFileVersionByMd5HashForUserAsync(md5Hash, callerUser.Id);
                if (checkIsFileWithHashExist != null)
                {
                    State.ErrorMessage = "This version of file already exist!";
                    State.TypeOfError = TypeOfServiceError.BadRequest;
                    return State;
                }

                string generateNameForAzureBlob = GenerateNameForTheAzureBlob(md5Hash, file.FileName, userEmail);
                string contentType;
                new FileExtensionContentTypeProvider().TryGetContentType(file.FileName, out contentType);
                if (contentType == null)
                    contentType = "none";

                var allNodesForUser = await _unitOfWork.NodeRepository.GetAllNodesForUser(callerUser.Id);
                var existedFile = allNodesForUser.FirstOrDefault(r => r.Name == file.FileName && r.IsDirectory == false);
                // If file already exist - add new version
                if (existedFile != null)
                {
                    await AddNewVersionOfFileAsync(file, existedFile, md5Hash,
                                  generateNameForAzureBlob);
                    return State;
                }

                // else just create as first file on the system
                var fileNode = new Node
                {
                    Created = DateTime.Now,
                    IsDirectory = false,
                    Owner = callerUser,
                    Folder = directoryWhereFileUploadTo,
                    Name = file.FileName,
                    FileVersions = new List<FileVersion>(),
                    ContentType = contentType,
                };
                var fileVersion = new FileVersion
                {
                    Node = fileNode,
                    Created = DateTime.Now,
                    MD5Hash = md5Hash,
                    PathToFile = generateNameForAzureBlob,
                    Size = file.Length,
                    VersionOfFile = 1
                };

                // Add to db
                _unitOfWork.NodeRepository.AddNode(fileNode);
                _unitOfWork.FileVersionRepository.AddFileVersion(fileVersion);
                directoryWhereFileUploadTo.Siblings.Add(fileNode);

                await _unitOfWork.CommitAsync();

                // Upload to azure blob
                await _blobService.UploadFileAsync(file, generateNameForAzureBlob);
                return State;
            }
            catch (Exception ex)
            {
                State.ErrorMessage = ex.Message;
                State.TypeOfError = TypeOfServiceError.ConnectionError;
                return null;
            }
        }

        #endregion

        #region Helpers methods 

        private async Task<ServiceState> AddNewVersionOfFileAsync(IFormFile newfile, Node existedFile, string hash, string generatedName)
        {
            int getLastVersionOfFile =
                      await _unitOfWork.FileVersionRepository.GetNumberOfLastVersionFile(existedFile);
            // Increment version of file
            getLastVersionOfFile++;
            var newFileVersion = new FileVersion
            {
                Node = existedFile,
                Created = DateTime.Now,
                MD5Hash = hash,
                PathToFile = generatedName,
                Size = newfile.Length,
                VersionOfFile = getLastVersionOfFile
            };
            _unitOfWork.FileVersionRepository.AddFileVersion(newFileVersion);
            await _unitOfWork.CommitAsync();
            await _blobService.UploadFileAsync(newfile, generatedName);
            return State;
        }
        private async Task<Tuple<Stream, NodeDto>> GetConcreteVersionOfFile(Node file, int versionOfFile)
        {
            var getVersionOfFile =
                await _unitOfWork.FileVersionRepository.GetFileVersionOfVersionNumber(file, versionOfFile);

            if (getVersionOfFile == null)
            {
                State.TypeOfError = TypeOfServiceError.NotFound;
                State.ErrorMessage = "Requeset version not found!";
                return Tuple.Create<Stream, NodeDto>(null, null);
            }
            var streamOfFileFromBlob = await _blobService.DownloadFileAsync(getVersionOfFile.PathToFile);
            if (streamOfFileFromBlob == null)
            {
                State.TypeOfError = TypeOfServiceError.ConnectionError;
                State.ErrorMessage = "Error with getting file from Azure blob storage!";
                return Tuple.Create<Stream, NodeDto>(null, null);
            }

            // Set start position of the stream
            streamOfFileFromBlob.Position = 0;
            return Tuple.Create(streamOfFileFromBlob, Mapper.Map<Node, NodeDto>(file));
        }
        private async Task<Tuple<Stream, NodeDto>> GetLastVersionOfFile(Node file)
        {
           
            var getLastVersionOfFile = await _unitOfWork.FileVersionRepository.GetLatestFileVersion(file);
            if (getLastVersionOfFile == null)
            {
                State.TypeOfError = TypeOfServiceError.NotFound;
                State.ErrorMessage = "Latest version of file not found!";
                return Tuple.Create<Stream, NodeDto>(null, null);
            }
            var streamOfFileFromBlob = await _blobService.DownloadFileAsync(getLastVersionOfFile.PathToFile);
            if (streamOfFileFromBlob == null)
            {
                State.TypeOfError = TypeOfServiceError.ConnectionError;
                State.ErrorMessage = "Error with getting file from Azure blob storage!";
                return Tuple.Create<Stream, NodeDto>(null, null);
            }

            // Set start position of the stream
            streamOfFileFromBlob.Position = 0;
            return Tuple.Create(streamOfFileFromBlob, Mapper.Map<Node, NodeDto>(file));
        }
        private bool ValidateNode(ServiceState modelState, Node node, ApplicationUser user)
        {
            if (node == null)
            {
                modelState.ErrorMessage = "Folder is not found!";
                modelState.TypeOfError = TypeOfServiceError.NotFound;
                return modelState.IsValid;
            }
            if (node.OwnerId != user.Id)
            {
                State.TypeOfError = TypeOfServiceError.Unathorized;
                modelState.ErrorMessage = "Access denied";

                return modelState.IsValid;
            }
            return modelState.IsValid;
        }
        private string GenerateNameForTheAzureBlob(string md5Hash, string fileName, string userEmail)
        {
            return $"{userEmail}_{md5Hash}_{fileName}";
        }
        private string GetMD5HashFromFile(IFormFile file)
        {
            string md5Hash;
            using (var md5 = MD5.Create())
            {
                using (var stream = file.OpenReadStream())
                {
                    var buffer = md5.ComputeHash(stream);
                    var sb = new StringBuilder();
                    foreach (byte t in buffer)
                    {
                        sb.Append(t.ToString("x2"));
                    }
                    md5Hash = sb.ToString();
                }
            }
            return md5Hash;
        }
        #endregion

    }
}